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
using static PayPlanner.Api.Services.Reports.ReportQueryPeriodResolver;
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

    private Task<bool> HasPaymentsPermissionAsync(
        Func<RolePermissionValues, bool> isAllowed,
        CancellationToken ct)
        => _db.HasUserSectionPermissionAsync(User, RolePermissionCatalog.Payments, isAllowed, ct);

    private async Task<DateTime> ResolveReportTodayAsync(CancellationToken ct)
    {
        var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct);
        return CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date;
    }

    private async Task<(DateTime Utc, DateTime Local, TimeZoneInfo TimeZone)> ResolveReportNowAsync(CancellationToken ct)
    {
        var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct);
        var nowUtc = NormalizeReportInstant(DateTime.UtcNow);
        return (nowUtc, CalendarTimeZoneHelper.ToUserLocal(nowUtc, timeZone), timeZone);
    }

    private async Task<(DateTime Start, DateTime End)> ResolveReportPeriodAsync(DateTime? from, DateTime? to, CancellationToken ct)
        => ResolveReportPeriod(from, to, await ResolveReportTodayAsync(ct));

    [HttpGet("legal-work")]
    public async Task<ActionResult<LegalWorkReportDto>> LegalWork(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? status,
        [FromQuery] int? responsibleUserId,
        [FromQuery] string? caseType,
        CancellationToken ct)
    {
        var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct);
        var reportToday = CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date;
        var (start, end) = ResolveReportPeriod(from, to, reportToday);

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
            cancellationToken => BuildLegalWorkReportAsync(start, end, reportToday, timeZone, filters, casesVisibility, tasksVisibility, accountsVisibility, cancellationToken),
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

        var now = await ResolveReportNowAsync(ct);
        return File(
            BuildLegalWorkCsv(report!, now.Utc, now.TimeZone),
            "text/csv; charset=utf-8",
            $"legal-work-report-{now.Local:yyyyMMdd-HHmm}.csv");
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

        var now = await ResolveReportNowAsync(ct);
        var fileName = $"legal-work-report-{now.Local:yyyyMMdd-HHmm}.xls";
        return File(
            Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(BuildLegalWorkExcelXml(report!, now.Utc, now.TimeZone))).ToArray(),
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

        var now = await ResolveReportNowAsync(ct);
        var pdf = await _pdfService.RenderAsync(BuildLegalWorkPrintHtml(report!, now.Utc, now.TimeZone), ct);
        return File(pdf, "application/pdf", $"legal-work-report-{now.Local:yyyyMMdd-HHmm}.pdf");
    }

    [HttpGet("management.xls")]
    public async Task<IActionResult> ExportManagementReportExcel(
        [FromQuery] string report,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] MoneyOperationSource? source,
        [FromQuery] string? accountName,
        [FromQuery] string? counterpartyName,
        [FromQuery] int? clientId,
        [FromQuery] int? caseId,
        [FromQuery] int? contractId,
        [FromQuery] int? incomeTypeId,
        [FromQuery] DateTime? asOf,
        [FromQuery] string? status,
        [FromQuery] int? responsibleUserId,
        [FromQuery] string? caseType,
        [FromQuery] int? lawyerUserId,
        [FromQuery] int? horizonDays,
        [FromQuery] string? deadlineType,
        [FromQuery] string? activity,
        [FromQuery] int? inactiveDays,
        [FromQuery] int? escalationDays,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        var (document, error) = await BuildManagementReportExportDocumentAsync(
            report,
            from,
            to,
            source,
            accountName,
            counterpartyName,
            clientId,
            caseId,
            contractId,
            incomeTypeId,
            asOf,
            status,
            responsibleUserId,
            caseType,
            lawyerUserId,
            horizonDays,
            deadlineType,
            activity,
            inactiveDays,
            escalationDays,
            limit,
            ct);
        if (error is not null)
        {
            return error;
        }

        var now = await ResolveReportNowAsync(ct);
        return File(
            Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(BuildReportExportExcelXml(document!, now.Utc, now.TimeZone))).ToArray(),
            "application/vnd.ms-excel; charset=utf-8",
            $"{document!.Slug}-{now.Local:yyyyMMdd-HHmm}.xls");
    }

    [HttpGet("management.pdf")]
    public async Task<IActionResult> ExportManagementReportPdf(
        [FromQuery] string report,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] MoneyOperationSource? source,
        [FromQuery] string? accountName,
        [FromQuery] string? counterpartyName,
        [FromQuery] int? clientId,
        [FromQuery] int? caseId,
        [FromQuery] int? contractId,
        [FromQuery] int? incomeTypeId,
        [FromQuery] DateTime? asOf,
        [FromQuery] string? status,
        [FromQuery] int? responsibleUserId,
        [FromQuery] string? caseType,
        [FromQuery] int? lawyerUserId,
        [FromQuery] int? horizonDays,
        [FromQuery] string? deadlineType,
        [FromQuery] string? activity,
        [FromQuery] int? inactiveDays,
        [FromQuery] int? escalationDays,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        if (_pdfService is null)
        {
            return Problem("Сервис формирования PDF недоступен.");
        }

        var (document, error) = await BuildManagementReportExportDocumentAsync(
            report,
            from,
            to,
            source,
            accountName,
            counterpartyName,
            clientId,
            caseId,
            contractId,
            incomeTypeId,
            asOf,
            status,
            responsibleUserId,
            caseType,
            lawyerUserId,
            horizonDays,
            deadlineType,
            activity,
            inactiveDays,
            escalationDays,
            limit,
            ct);
        if (error is not null)
        {
            return error;
        }

        var now = await ResolveReportNowAsync(ct);
        var pdf = await _pdfService.RenderAsync(BuildReportExportPrintHtml(document!, now.Utc, now.TimeZone), ct);
        return File(pdf, "application/pdf", $"{document!.Slug}-{now.Local:yyyyMMdd-HHmm}.pdf");
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

        var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct);
        var (start, end) = ResolveReportPeriod(from, to, CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date);
        var filters = new LegalWorkReportFilters(NormalizeOptional(status), responsibleUserId, NormalizeOptional(caseType));
        var effectiveLimit = Math.Clamp(limit ?? 100, 1, 500);

        var report = await SqliteRetryHelper.ExecuteAsync(
            cancellationToken => BuildCaseFinanceReportAsync(start, end, timeZone, filters, effectiveLimit, casesVisibility, accountsVisibility, cancellationToken),
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

        var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct);
        var reportToday = CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date;
        var (start, end) = ResolveReportPeriod(from, to, reportToday);
        var filters = new LegalWorkReportFilters(NormalizeOptional(status), responsibleUserId, NormalizeOptional(caseType));
        var effectiveLimit = Math.Clamp(limit ?? 100, 1, 500);

        var report = await SqliteRetryHelper.ExecuteAsync(
            cancellationToken => BuildCaseSuccessReportAsync(start, end, reportToday, timeZone, filters, effectiveLimit, casesVisibility, cancellationToken),
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

        var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct);
        var reportToday = CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date;
        var (start, end) = ResolveReportPeriod(from, to, reportToday);
        var filters = new LegalWorkReportFilters(NormalizeOptional(status), responsibleUserId, NormalizeOptional(caseType));
        var report = await SqliteRetryHelper.ExecuteAsync(
            cancellationToken => BuildLegalWorkReportAsync(start, end, reportToday, timeZone, filters, casesVisibility, tasksVisibility, accountsVisibility, cancellationToken),
            _logger,
            ct);

        return (report, null);
    }

    private async Task<(ReportExportDocument? Document, IActionResult? Error)> BuildManagementReportExportDocumentAsync(
        string? report,
        DateTime? from,
        DateTime? to,
        MoneyOperationSource? source,
        string? accountName,
        string? counterpartyName,
        int? clientId,
        int? caseId,
        int? contractId,
        int? incomeTypeId,
        DateTime? asOf,
        string? status,
        int? responsibleUserId,
        string? caseType,
        int? lawyerUserId,
        int? horizonDays,
        string? deadlineType,
        string? activity,
        int? inactiveDays,
        int? escalationDays,
        int? limit,
        CancellationToken ct)
    {
        if (!await CanExportReportsAsync(ct))
        {
            return (null, Forbid());
        }

        var normalizedReport = (NormalizeOptional(report) ?? string.Empty).ToLowerInvariant();
        var normalizedAccountName = NormalizeOptional(accountName);
        var normalizedCounterpartyName = NormalizeOptional(counterpartyName);
        var effectiveClientId = clientId.HasValue && clientId.Value > 0 ? clientId.Value : (int?)null;
        var effectiveCaseId = caseId.HasValue && caseId.Value > 0 ? caseId.Value : (int?)null;
        var effectiveContractId = contractId.HasValue && contractId.Value > 0 ? contractId.Value : (int?)null;
        var effectiveIncomeTypeId = incomeTypeId.HasValue && incomeTypeId.Value > 0 ? incomeTypeId.Value : (int?)null;
        var reportToday = await ResolveReportTodayAsync(ct);
        var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct);
        var effectiveAsOf = asOf?.Date ?? reportToday;
        var effectiveLimit = Math.Clamp(limit ?? 100, 1, 500);

        switch (normalizedReport)
        {
            case "case-finance":
                {
                    var casesVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Cases, ct);
                    var accountsVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Accounts, ct);
                    if (casesVisibility is null || accountsVisibility is null)
                    {
                        return (null, Unauthorized());
                    }

                    var (start, end) = ResolveReportPeriod(from, to, reportToday);
                    var filters = new LegalWorkReportFilters(NormalizeOptional(status), responsibleUserId, NormalizeOptional(caseType));
                    var reportDto = await SqliteRetryHelper.ExecuteAsync(
                        cancellationToken => BuildCaseFinanceReportAsync(start, end, timeZone, filters, effectiveLimit, casesVisibility, accountsVisibility, cancellationToken),
                        _logger,
                        ct);
                    return (BuildCaseFinanceExportDocument(reportDto), null);
                }

            case "cash-flow":
                {
                    var paymentsVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Payments, ct);
                    if (paymentsVisibility is null)
                    {
                        return (null, Unauthorized());
                    }

                    var (start, end) = ResolveReportPeriod(from, to, reportToday);
                    var reportDto = await SqliteRetryHelper.ExecuteAsync(
                        cancellationToken => BuildCashFlowReportAsync(start, end, source, normalizedAccountName, normalizedCounterpartyName, effectiveClientId, effectiveCaseId, effectiveContractId, effectiveIncomeTypeId, paymentsVisibility, cancellationToken),
                        _logger,
                        ct);
                    return (BuildCashFlowExportDocument(reportDto), null);
                }

            case "cash":
                {
                    if (!await HasPaymentsPermissionAsync(static permission => permission.CanExport, ct))
                    {
                        return (null, Forbid());
                    }

                    var (start, end) = ResolveReportPeriod(from, to, reportToday);
                    var reportDto = await SqliteRetryHelper.ExecuteAsync(
                        cancellationToken => BuildCashBookReportAsync(start, end, cancellationToken),
                        _logger,
                        ct);
                    return (BuildCashBookExportDocument(reportDto), null);
                }

            case "bank":
                {
                    var paymentsVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Payments, ct);
                    if (paymentsVisibility is null)
                    {
                        return (null, Unauthorized());
                    }

                    var (start, end) = ResolveReportPeriod(from, to, reportToday);
                    var reportDto = await SqliteRetryHelper.ExecuteAsync(
                        cancellationToken => BuildCashFlowReportAsync(start, end, MoneyOperationSource.Bank, normalizedAccountName, normalizedCounterpartyName, effectiveClientId, effectiveCaseId, effectiveContractId, effectiveIncomeTypeId, paymentsVisibility, cancellationToken),
                        _logger,
                        ct);
                    return (BuildBankExportDocument(reportDto), null);
                }

            case "plan-fact":
                {
                    var paymentsVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Payments, ct);
                    if (paymentsVisibility is null)
                    {
                        return (null, Unauthorized());
                    }

                    var (start, end) = ResolveReportPeriod(from, to, reportToday);
                    var reportDto = await SqliteRetryHelper.ExecuteAsync(
                        cancellationToken => BuildPlanFactReportAsync(start, end, source, normalizedAccountName, normalizedCounterpartyName, effectiveClientId, effectiveCaseId, effectiveContractId, effectiveIncomeTypeId, paymentsVisibility, cancellationToken),
                        _logger,
                        ct);
                    return (BuildPlanFactExportDocument(reportDto), null);
                }

            case "advances":
                {
                    var paymentsVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Payments, ct);
                    if (paymentsVisibility is null)
                    {
                        return (null, Unauthorized());
                    }

                    var (start, end) = ResolveReportPeriod(from, to, reportToday);
                    var reportDto = await SqliteRetryHelper.ExecuteAsync(
                        cancellationToken => BuildAdvanceReportAsync(start, end, source, normalizedAccountName, normalizedCounterpartyName, effectiveClientId, effectiveCaseId, effectiveContractId, effectiveIncomeTypeId, paymentsVisibility, cancellationToken),
                        _logger,
                        ct);
                    return (BuildAdvanceExportDocument(reportDto), null);
                }

            case "payment-priorities":
                {
                    var paymentsVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Payments, ct);
                    if (paymentsVisibility is null)
                    {
                        return (null, Unauthorized());
                    }

                    var (start, end) = ResolveReportPeriod(from, to, reportToday);
                    var reportDto = await SqliteRetryHelper.ExecuteAsync(
                        cancellationToken => BuildPaymentPriorityReportAsync(start, end, source, normalizedAccountName, normalizedCounterpartyName, effectiveClientId, effectiveCaseId, effectiveContractId, effectiveIncomeTypeId, paymentsVisibility, cancellationToken),
                        _logger,
                        ct);
                    return (BuildPaymentPriorityExportDocument(reportDto), null);
                }

            case "paid-accounts":
                {
                    var paymentsVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Payments, ct);
                    if (paymentsVisibility is null)
                    {
                        return (null, Unauthorized());
                    }

                    var (start, end) = ResolveReportPeriod(from, to, reportToday);
                    var reportDto = await SqliteRetryHelper.ExecuteAsync(
                        cancellationToken => BuildPaidAccountReportAsync(start, end, source, normalizedAccountName, normalizedCounterpartyName, effectiveClientId, effectiveCaseId, effectiveContractId, effectiveIncomeTypeId, paymentsVisibility, cancellationToken),
                        _logger,
                        ct);
                    return (BuildPaidAccountExportDocument(reportDto), null);
                }

            case "paid-invoices":
                {
                    var paymentsVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Payments, ct);
                    if (paymentsVisibility is null)
                    {
                        return (null, Unauthorized());
                    }

                    var (start, end) = ResolveReportPeriod(from, to, reportToday);
                    var reportDto = await SqliteRetryHelper.ExecuteAsync(
                        cancellationToken => BuildPaidInvoiceReportAsync(start, end, source, normalizedAccountName, normalizedCounterpartyName, effectiveClientId, effectiveCaseId, effectiveContractId, effectiveIncomeTypeId, paymentsVisibility, cancellationToken),
                        _logger,
                        ct);
                    return (BuildPaidInvoiceExportDocument(reportDto), null);
                }

            case "invoice-statuses":
                {
                    var accountsVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Accounts, ct);
                    if (accountsVisibility is null)
                    {
                        return (null, Unauthorized());
                    }

                    var (start, end) = ResolveReportPeriod(from, to, reportToday);
                    var reportDto = await SqliteRetryHelper.ExecuteAsync(
                        cancellationToken => BuildInvoiceStatusReportAsync(start, end, accountsVisibility, cancellationToken),
                        _logger,
                        ct);
                    return (BuildInvoiceStatusExportDocument(reportDto), null);
                }

            case "act-statuses":
                {
                    var actsVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Acts, ct);
                    if (actsVisibility is null)
                    {
                        return (null, Unauthorized());
                    }

                    var (start, end) = ResolveReportPeriod(from, to, reportToday);
                    var reportDto = await SqliteRetryHelper.ExecuteAsync(
                        cancellationToken => BuildActStatusReportAsync(start, end, actsVisibility, cancellationToken),
                        _logger,
                        ct);
                    return (BuildActStatusExportDocument(reportDto), null);
                }

            case "contract-statuses":
                {
                    var contractsVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Contracts, ct);
                    if (contractsVisibility is null)
                    {
                        return (null, Unauthorized());
                    }

                    var (start, end) = ResolveReportPeriod(from, to, reportToday);
                    var reportDto = await SqliteRetryHelper.ExecuteAsync(
                        cancellationToken => BuildContractStatusReportAsync(start, end, contractsVisibility, cancellationToken),
                        _logger,
                        ct);
                    return (BuildContractStatusExportDocument(reportDto), null);
                }

            case "categories":
                {
                    var paymentsVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Payments, ct);
                    if (paymentsVisibility is null)
                    {
                        return (null, Unauthorized());
                    }

                    var (start, end) = ResolveReportPeriod(from, to, reportToday);
                    var reportDto = await SqliteRetryHelper.ExecuteAsync(
                        cancellationToken => BuildCategoryReportAsync(start, end, source, normalizedAccountName, normalizedCounterpartyName, effectiveClientId, effectiveCaseId, effectiveContractId, effectiveIncomeTypeId, paymentsVisibility, cancellationToken),
                        _logger,
                        ct);
                    return (BuildCategoryExportDocument(reportDto), null);
                }

            case "counterparties":
                {
                    var paymentsVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Payments, ct);
                    if (paymentsVisibility is null)
                    {
                        return (null, Unauthorized());
                    }

                    var (start, end) = ResolveReportPeriod(from, to, reportToday);
                    var reportDto = await SqliteRetryHelper.ExecuteAsync(
                        cancellationToken => BuildCounterpartyReportAsync(start, end, source, normalizedAccountName, normalizedCounterpartyName, effectiveClientId, effectiveCaseId, effectiveContractId, effectiveIncomeTypeId, paymentsVisibility, cancellationToken),
                        _logger,
                        ct);
                    return (BuildCounterpartyExportDocument(reportDto), null);
                }

            case "projects":
                {
                    var paymentsVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Payments, ct);
                    if (paymentsVisibility is null)
                    {
                        return (null, Unauthorized());
                    }

                    var (start, end) = ResolveReportPeriod(from, to, reportToday);
                    var reportDto = await SqliteRetryHelper.ExecuteAsync(
                        cancellationToken => BuildProjectReportAsync(start, end, source, normalizedAccountName, normalizedCounterpartyName, effectiveClientId, effectiveCaseId, effectiveContractId, effectiveIncomeTypeId, paymentsVisibility, cancellationToken),
                        _logger,
                        ct);
                    return (BuildProjectExportDocument(reportDto), null);
                }

            case "directions":
                {
                    var paymentsVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Payments, ct);
                    if (paymentsVisibility is null)
                    {
                        return (null, Unauthorized());
                    }

                    var (start, end) = ResolveReportPeriod(from, to, reportToday);
                    var reportDto = await SqliteRetryHelper.ExecuteAsync(
                        cancellationToken => BuildDirectionReportAsync(start, end, source, normalizedAccountName, normalizedCounterpartyName, effectiveClientId, effectiveCaseId, effectiveContractId, effectiveIncomeTypeId, paymentsVisibility, cancellationToken),
                        _logger,
                        ct);
                    return (BuildDirectionExportDocument(reportDto), null);
                }

            case "sales-points":
                {
                    var paymentsVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Payments, ct);
                    if (paymentsVisibility is null)
                    {
                        return (null, Unauthorized());
                    }

                    var (start, end) = ResolveReportPeriod(from, to, reportToday);
                    var reportDto = await SqliteRetryHelper.ExecuteAsync(
                        cancellationToken => BuildSalesPointReportAsync(start, end, source, normalizedAccountName, normalizedCounterpartyName, effectiveClientId, effectiveCaseId, effectiveContractId, effectiveIncomeTypeId, paymentsVisibility, cancellationToken),
                        _logger,
                        ct);
                    return (BuildSalesPointExportDocument(reportDto), null);
                }

            case "vat":
                {
                    var paymentsVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Payments, ct);
                    if (paymentsVisibility is null)
                    {
                        return (null, Unauthorized());
                    }

                    var (start, end) = ResolveReportPeriod(from, to, reportToday);
                    var reportDto = await SqliteRetryHelper.ExecuteAsync(
                        cancellationToken => BuildVatReportAsync(start, end, source, normalizedAccountName, normalizedCounterpartyName, effectiveClientId, effectiveCaseId, effectiveContractId, effectiveIncomeTypeId, paymentsVisibility, cancellationToken),
                        _logger,
                        ct);
                    return (BuildVatExportDocument(reportDto), null);
                }

            case "payment-orders":
                {
                    var paymentsVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Payments, ct);
                    if (paymentsVisibility is null)
                    {
                        return (null, Unauthorized());
                    }

                    var (start, end) = ResolveReportPeriod(from, to, reportToday);
                    var reportDto = await SqliteRetryHelper.ExecuteAsync(
                        cancellationToken => BuildPaymentOrderReportAsync(start, end, source, normalizedAccountName, normalizedCounterpartyName, effectiveClientId, effectiveCaseId, effectiveContractId, effectiveIncomeTypeId, paymentsVisibility, cancellationToken),
                        _logger,
                        ct);
                    return (BuildPaymentOrderExportDocument(reportDto), null);
                }

            case "payment-purposes":
                {
                    var paymentsVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Payments, ct);
                    if (paymentsVisibility is null)
                    {
                        return (null, Unauthorized());
                    }

                    var (start, end) = ResolveReportPeriod(from, to, reportToday);
                    var reportDto = await SqliteRetryHelper.ExecuteAsync(
                        cancellationToken => BuildPaymentPurposeReportAsync(start, end, source, normalizedAccountName, normalizedCounterpartyName, effectiveClientId, effectiveCaseId, effectiveContractId, effectiveIncomeTypeId, paymentsVisibility, cancellationToken),
                        _logger,
                        ct);
                    return (BuildPaymentPurposeExportDocument(reportDto), null);
                }

            case "match-statuses":
                {
                    var paymentsVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Payments, ct);
                    if (paymentsVisibility is null)
                    {
                        return (null, Unauthorized());
                    }

                    var (start, end) = ResolveReportPeriod(from, to, reportToday);
                    var reportDto = await SqliteRetryHelper.ExecuteAsync(
                        cancellationToken => BuildMatchStatusReportAsync(start, end, source, normalizedAccountName, normalizedCounterpartyName, effectiveClientId, effectiveCaseId, effectiveContractId, effectiveIncomeTypeId, paymentsVisibility, cancellationToken),
                        _logger,
                        ct);
                    return (BuildMatchStatusExportDocument(reportDto), null);
                }

            case "journal-statuses":
                {
                    var paymentsVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Payments, ct);
                    if (paymentsVisibility is null)
                    {
                        return (null, Unauthorized());
                    }

                    var (start, end) = ResolveReportPeriod(from, to, reportToday);
                    var reportDto = await SqliteRetryHelper.ExecuteAsync(
                        cancellationToken => BuildJournalStatusReportAsync(start, end, source, normalizedAccountName, normalizedCounterpartyName, effectiveClientId, effectiveCaseId, effectiveContractId, effectiveIncomeTypeId, paymentsVisibility, cancellationToken),
                        _logger,
                        ct);
                    return (BuildJournalStatusExportDocument(reportDto), null);
                }

            case "currencies":
                {
                    var paymentsVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Payments, ct);
                    if (paymentsVisibility is null)
                    {
                        return (null, Unauthorized());
                    }

                    var (start, end) = ResolveReportPeriod(from, to, reportToday);
                    var reportDto = await SqliteRetryHelper.ExecuteAsync(
                        cancellationToken => BuildCurrencyReportAsync(start, end, source, normalizedAccountName, normalizedCounterpartyName, effectiveClientId, effectiveCaseId, effectiveContractId, effectiveIncomeTypeId, paymentsVisibility, cancellationToken),
                        _logger,
                        ct);
                    return (BuildCurrencyExportDocument(reportDto), null);
                }

            case "cash-orders":
                {
                    if (!await HasPaymentsPermissionAsync(static permission => permission.CanExport, ct))
                    {
                        return (null, Forbid());
                    }

                    var (start, end) = ResolveReportPeriod(from, to, reportToday);
                    var reportDto = await SqliteRetryHelper.ExecuteAsync(
                        cancellationToken => BuildCashOrderReportAsync(start, end, cancellationToken),
                        _logger,
                        ct);
                    return (BuildCashOrderExportDocument(reportDto), null);
                }

            case "bank-statement-imports":
                {
                    var (start, end) = ResolveReportPeriod(from, to, reportToday);
                    var reportDto = await SqliteRetryHelper.ExecuteAsync(
                        cancellationToken => BuildBankStatementImportReportAsync(start, end, timeZone, cancellationToken),
                        _logger,
                        ct);
                    return (BuildBankStatementImportExportDocument(reportDto), null);
                }

            case "spreadsheet-imports":
                {
                    var (start, end) = ResolveReportPeriod(from, to, reportToday);
                    var reportDto = await SqliteRetryHelper.ExecuteAsync(
                        cancellationToken => BuildSpreadsheetImportReportAsync(start, end, timeZone, cancellationToken),
                        _logger,
                        ct);
                    return (BuildSpreadsheetImportExportDocument(reportDto), null);
                }

            case "case-success":
                {
                    var casesVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Cases, ct);
                    if (casesVisibility is null)
                    {
                        return (null, Unauthorized());
                    }

                    var (start, end) = ResolveReportPeriod(from, to, reportToday);
                    var filters = new LegalWorkReportFilters(NormalizeOptional(status), responsibleUserId, NormalizeOptional(caseType));
                    var reportDto = await SqliteRetryHelper.ExecuteAsync(
                        cancellationToken => BuildCaseSuccessReportAsync(start, end, reportToday, timeZone, filters, effectiveLimit, casesVisibility, cancellationToken),
                        _logger,
                        ct);
                    return (BuildCaseSuccessExportDocument(reportDto), null);
                }

            case "current-cases":
                {
                    var casesVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Cases, ct);
                    var tasksVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Tasks, ct);
                    if (casesVisibility is null || tasksVisibility is null)
                    {
                        return (null, Unauthorized());
                    }

                    var filters = new CurrentCasesReportFilters(NormalizeOptional(status), responsibleUserId, NormalizeOptional(caseType));
                    var reportDto = await SqliteRetryHelper.ExecuteAsync(
                        cancellationToken => BuildCurrentCasesReportAsync(effectiveAsOf, timeZone, filters, effectiveLimit, casesVisibility, tasksVisibility, cancellationToken),
                        _logger,
                        ct);
                    return (BuildCurrentCasesExportDocument(reportDto), null);
                }

            case "lawyer-workload":
                {
                    var casesVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Cases, ct);
                    var tasksVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Tasks, ct);
                    if (casesVisibility is null || tasksVisibility is null)
                    {
                        return (null, Unauthorized());
                    }

                    var effectiveHorizonDays = Math.Clamp(horizonDays ?? 7, 1, 60);
                    var reportDto = await SqliteRetryHelper.ExecuteAsync(
                        cancellationToken => BuildLawyerWorkloadReportAsync(effectiveAsOf, effectiveHorizonDays, lawyerUserId, effectiveLimit, casesVisibility, tasksVisibility, cancellationToken),
                        _logger,
                        ct);
                    return (BuildLawyerWorkloadExportDocument(reportDto, timeZone), null);
                }

            case "overdue-deadlines":
                {
                    var casesVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Cases, ct);
                    if (casesVisibility is null)
                    {
                        return (null, Unauthorized());
                    }

                    var reportDto = await SqliteRetryHelper.ExecuteAsync(
                        cancellationToken => BuildOverdueProceduralDeadlinesReportAsync(effectiveAsOf, NormalizeOptional(deadlineType), responsibleUserId, effectiveLimit, casesVisibility, cancellationToken),
                        _logger,
                        ct);
                    return (BuildOverdueDeadlinesExportDocument(reportDto, timeZone), null);
                }

            case "case-activity":
                {
                    var casesVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Cases, ct);
                    if (casesVisibility is null)
                    {
                        return (null, Unauthorized());
                    }

                    var settings = await GetCaseActivityReportSettingsAsync(ct);
                    var effectiveInactiveDays = Math.Clamp(inactiveDays ?? settings.InactivityDays, 1, 365);
                    var effectiveEscalationDays = Math.Clamp(escalationDays ?? settings.EscalationDays, effectiveInactiveDays, 365);
                    var effectiveActivityLimit = Math.Clamp(limit ?? 50, 1, 200);
                    var normalizedActivity = NormalizeCaseActivityFilter(activity);
                    var reportDto = await SqliteRetryHelper.ExecuteAsync(
                        cancellationToken => BuildCaseActivityReportAsync(effectiveInactiveDays, effectiveEscalationDays, normalizedActivity, effectiveActivityLimit, casesVisibility, cancellationToken),
                        _logger,
                        ct);
                    return (BuildCaseActivityExportDocument(reportDto, timeZone), null);
                }

            case "cash-flow-forecast":
                {
                    var paymentsVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Payments, ct);
                    if (paymentsVisibility is null)
                    {
                        return (null, Unauthorized());
                    }

                    var (start, end) = ResolveReportPeriod(from, to, reportToday);
                    var reportDto = await SqliteRetryHelper.ExecuteAsync(
                        cancellationToken => BuildCashFlowForecastReportAsync(start, end, normalizedAccountName, normalizedCounterpartyName, effectiveClientId, effectiveCaseId, effectiveContractId, effectiveIncomeTypeId, paymentsVisibility, cancellationToken),
                        _logger,
                        ct);
                    return (BuildCashFlowForecastExportDocument(reportDto), null);
                }

            default:
                return (null, BadRequest("Неизвестный управленческий отчет для выгрузки."));
        }
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

        var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct);
        var effectiveAsOf = asOf?.Date ?? CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date;
        var effectiveLimit = Math.Clamp(limit ?? 100, 1, 500);
        var filters = new CurrentCasesReportFilters(
            NormalizeOptional(status),
            responsibleUserId,
            NormalizeOptional(caseType));

        var report = await SqliteRetryHelper.ExecuteAsync(
            cancellationToken => BuildCurrentCasesReportAsync(
                effectiveAsOf,
                timeZone,
                filters,
                effectiveLimit,
                casesVisibility,
                tasksVisibility,
                cancellationToken),
            _logger,
            ct);

        return Ok(report);
    }

    [HttpGet("tasks")]
    public async Task<ActionResult<TaskReportDto>> Tasks(
        [FromQuery] DateTime? asOf,
        [FromQuery] string? scope,
        [FromQuery] int? assigneeUserId,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        if (!await CanViewReportsAsync(ct))
        {
            return Forbid();
        }

        var tasksVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Tasks, ct);
        if (tasksVisibility is null)
        {
            return Unauthorized();
        }

        var effectiveAsOf = asOf?.Date ?? await ResolveReportTodayAsync(ct);
        var effectiveScope = NormalizeTaskReportScope(scope);
        var effectiveLimit = Math.Clamp(limit ?? 150, 1, 500);

        var report = await SqliteRetryHelper.ExecuteAsync(
            cancellationToken => BuildTaskReportAsync(
                effectiveAsOf,
                effectiveScope,
                assigneeUserId,
                effectiveLimit,
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

        var effectiveAsOf = asOf?.Date ?? await ResolveReportTodayAsync(ct);
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

        var effectiveAsOf = asOf?.Date ?? await ResolveReportTodayAsync(ct);
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
        [FromQuery] string? counterpartyName,
        [FromQuery] int? clientId,
        [FromQuery] int? caseId,
        [FromQuery] int? contractId,
        [FromQuery] int? incomeTypeId,
        CancellationToken ct)
    {
        var (start, end) = await ResolveReportPeriodAsync(from, to, ct);

        var normalizedAccountName = string.IsNullOrWhiteSpace(accountName)
            ? null
            : accountName.Trim();
        var normalizedCounterpartyName = NormalizeOptional(counterpartyName);
        var effectiveClientId = clientId.HasValue && clientId.Value > 0 ? clientId.Value : (int?)null;
        var effectiveCaseId = caseId.HasValue && caseId.Value > 0 ? caseId.Value : (int?)null;
        var effectiveContractId = contractId.HasValue && contractId.Value > 0 ? contractId.Value : (int?)null;
        var effectiveIncomeTypeId = incomeTypeId.HasValue && incomeTypeId.Value > 0 ? incomeTypeId.Value : (int?)null;

        return await ExecuteVisibleReportAsync(
            RolePermissionCatalog.Payments,
            (visibility, cancellationToken) => BuildCashFlowReportAsync(
                start,
                end,
                source,
                normalizedAccountName,
                normalizedCounterpartyName,
                effectiveClientId,
                effectiveCaseId,
                effectiveContractId,
                effectiveIncomeTypeId,
                visibility,
                cancellationToken),
            ct);
    }

    [HttpGet("cash-flow-forecast")]
    public async Task<ActionResult<CashFlowForecastReportDto>> CashFlowForecast(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? accountName,
        [FromQuery] string? counterpartyName,
        [FromQuery] int? clientId,
        [FromQuery] int? caseId,
        [FromQuery] int? contractId,
        [FromQuery] int? incomeTypeId,
        CancellationToken ct)
    {
        var (start, end) = await ResolveReportPeriodAsync(from, to, ct);

        var normalizedAccountName = string.IsNullOrWhiteSpace(accountName)
            ? null
            : accountName.Trim();
        var normalizedCounterpartyName = NormalizeOptional(counterpartyName);
        var effectiveClientId = clientId.HasValue && clientId.Value > 0 ? clientId.Value : (int?)null;
        var effectiveCaseId = caseId.HasValue && caseId.Value > 0 ? caseId.Value : (int?)null;
        var effectiveContractId = contractId.HasValue && contractId.Value > 0 ? contractId.Value : (int?)null;
        var effectiveIncomeTypeId = incomeTypeId.HasValue && incomeTypeId.Value > 0 ? incomeTypeId.Value : (int?)null;

        return await ExecuteVisibleReportAsync(
            RolePermissionCatalog.Payments,
            (visibility, cancellationToken) => BuildCashFlowForecastReportAsync(
                start,
                end,
                normalizedAccountName,
                normalizedCounterpartyName,
                effectiveClientId,
                effectiveCaseId,
                effectiveContractId,
                effectiveIncomeTypeId,
                visibility,
                cancellationToken),
            ct);
    }

    [HttpGet("categories")]
    public async Task<ActionResult<MoneyCategoryReportDto>> Categories(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] MoneyOperationSource? source,
        [FromQuery] string? accountName,
        [FromQuery] string? counterpartyName,
        [FromQuery] int? clientId,
        [FromQuery] int? caseId,
        [FromQuery] int? contractId,
        [FromQuery] int? incomeTypeId,
        CancellationToken ct)
    {
        var (start, end) = await ResolveReportPeriodAsync(from, to, ct);

        return await ExecuteVisibleReportAsync(
            RolePermissionCatalog.Payments,
            (visibility, cancellationToken) => BuildCategoryReportAsync(start, end, source, NormalizeOptional(accountName), NormalizeOptional(counterpartyName), clientId, caseId, contractId, incomeTypeId, visibility, cancellationToken),
            ct);
    }

    [HttpGet("counterparties")]
    public async Task<ActionResult<CounterpartyReportDto>> Counterparties(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] MoneyOperationSource? source,
        [FromQuery] string? accountName,
        [FromQuery] string? counterpartyName,
        [FromQuery] int? clientId,
        [FromQuery] int? caseId,
        [FromQuery] int? contractId,
        [FromQuery] int? incomeTypeId,
        CancellationToken ct)
    {
        var (start, end) = await ResolveReportPeriodAsync(from, to, ct);

        return await ExecuteVisibleReportAsync(
            RolePermissionCatalog.Payments,
            (visibility, cancellationToken) => BuildCounterpartyReportAsync(start, end, source, NormalizeOptional(accountName), NormalizeOptional(counterpartyName), clientId, caseId, contractId, incomeTypeId, visibility, cancellationToken),
            ct);
    }

    [HttpGet("projects")]
    public async Task<ActionResult<ProjectReportDto>> Projects(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] MoneyOperationSource? source,
        [FromQuery] string? accountName,
        [FromQuery] string? counterpartyName,
        [FromQuery] int? clientId,
        [FromQuery] int? caseId,
        [FromQuery] int? contractId,
        [FromQuery] int? incomeTypeId,
        CancellationToken ct)
    {
        var (start, end) = await ResolveReportPeriodAsync(from, to, ct);

        return await ExecuteVisibleReportAsync(
            RolePermissionCatalog.Payments,
            (visibility, cancellationToken) => BuildProjectReportAsync(start, end, source, NormalizeOptional(accountName), NormalizeOptional(counterpartyName), clientId, caseId, contractId, incomeTypeId, visibility, cancellationToken),
            ct);
    }

    [HttpGet("directions")]
    public async Task<ActionResult<DirectionReportDto>> Directions(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] MoneyOperationSource? source,
        [FromQuery] string? accountName,
        [FromQuery] string? counterpartyName,
        [FromQuery] int? clientId,
        [FromQuery] int? caseId,
        [FromQuery] int? contractId,
        [FromQuery] int? incomeTypeId,
        CancellationToken ct)
    {
        var (start, end) = await ResolveReportPeriodAsync(from, to, ct);

        return await ExecuteVisibleReportAsync(
            RolePermissionCatalog.Payments,
            (visibility, cancellationToken) => BuildDirectionReportAsync(start, end, source, NormalizeOptional(accountName), NormalizeOptional(counterpartyName), clientId, caseId, contractId, incomeTypeId, visibility, cancellationToken),
            ct);
    }

    [HttpGet("sales-points")]
    public async Task<ActionResult<SalesPointReportDto>> SalesPoints(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] MoneyOperationSource? source,
        [FromQuery] string? accountName,
        [FromQuery] string? counterpartyName,
        [FromQuery] int? clientId,
        [FromQuery] int? caseId,
        [FromQuery] int? contractId,
        [FromQuery] int? incomeTypeId,
        CancellationToken ct)
    {
        var (start, end) = await ResolveReportPeriodAsync(from, to, ct);

        return await ExecuteVisibleReportAsync(
            RolePermissionCatalog.Payments,
            (visibility, cancellationToken) => BuildSalesPointReportAsync(start, end, source, NormalizeOptional(accountName), NormalizeOptional(counterpartyName), clientId, caseId, contractId, incomeTypeId, visibility, cancellationToken),
            ct);
    }

    [HttpGet("vat")]
    public async Task<ActionResult<VatReportDto>> Vat(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] MoneyOperationSource? source,
        [FromQuery] string? accountName,
        [FromQuery] string? counterpartyName,
        [FromQuery] int? clientId,
        [FromQuery] int? caseId,
        [FromQuery] int? contractId,
        [FromQuery] int? incomeTypeId,
        CancellationToken ct)
    {
        var (start, end) = await ResolveReportPeriodAsync(from, to, ct);

        return await ExecuteVisibleReportAsync(
            RolePermissionCatalog.Payments,
            (visibility, cancellationToken) => BuildVatReportAsync(start, end, source, NormalizeOptional(accountName), NormalizeOptional(counterpartyName), clientId, caseId, contractId, incomeTypeId, visibility, cancellationToken),
            ct);
    }

    [HttpGet("plan-fact")]
    public async Task<ActionResult<PlanFactReportDto>> PlanFact(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] MoneyOperationSource? source,
        [FromQuery] string? accountName,
        [FromQuery] string? counterpartyName,
        [FromQuery] int? clientId,
        [FromQuery] int? caseId,
        [FromQuery] int? contractId,
        [FromQuery] int? incomeTypeId,
        CancellationToken ct)
    {
        var (start, end) = await ResolveReportPeriodAsync(from, to, ct);

        return await ExecuteVisibleReportAsync(
            RolePermissionCatalog.Payments,
            (visibility, cancellationToken) => BuildPlanFactReportAsync(start, end, source, NormalizeOptional(accountName), NormalizeOptional(counterpartyName), clientId, caseId, contractId, incomeTypeId, visibility, cancellationToken),
            ct);
    }

    [HttpGet("advances")]
    public async Task<ActionResult<AdvanceReportDto>> Advances(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] MoneyOperationSource? source,
        [FromQuery] string? accountName,
        [FromQuery] string? counterpartyName,
        [FromQuery] int? clientId,
        [FromQuery] int? caseId,
        [FromQuery] int? contractId,
        [FromQuery] int? incomeTypeId,
        CancellationToken ct)
    {
        var (start, end) = await ResolveReportPeriodAsync(from, to, ct);

        return await ExecuteVisibleReportAsync(
            RolePermissionCatalog.Payments,
            (visibility, cancellationToken) => BuildAdvanceReportAsync(start, end, source, NormalizeOptional(accountName), NormalizeOptional(counterpartyName), clientId, caseId, contractId, incomeTypeId, visibility, cancellationToken),
            ct);
    }

    [HttpGet("payment-priorities")]
    public async Task<ActionResult<PaymentPriorityReportDto>> PaymentPriorities(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] MoneyOperationSource? source,
        [FromQuery] string? accountName,
        [FromQuery] string? counterpartyName,
        [FromQuery] int? clientId,
        [FromQuery] int? caseId,
        [FromQuery] int? contractId,
        [FromQuery] int? incomeTypeId,
        CancellationToken ct)
    {
        var (start, end) = await ResolveReportPeriodAsync(from, to, ct);

        return await ExecuteVisibleReportAsync(
            RolePermissionCatalog.Payments,
            (visibility, cancellationToken) => BuildPaymentPriorityReportAsync(start, end, source, NormalizeOptional(accountName), NormalizeOptional(counterpartyName), clientId, caseId, contractId, incomeTypeId, visibility, cancellationToken),
            ct);
    }

    [HttpGet("paid-accounts")]
    public async Task<ActionResult<PaidAccountReportDto>> PaidAccounts(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] MoneyOperationSource? source,
        [FromQuery] string? accountName,
        [FromQuery] string? counterpartyName,
        [FromQuery] int? clientId,
        [FromQuery] int? caseId,
        [FromQuery] int? contractId,
        [FromQuery] int? incomeTypeId,
        CancellationToken ct)
    {
        var (start, end) = await ResolveReportPeriodAsync(from, to, ct);

        return await ExecuteVisibleReportAsync(
            RolePermissionCatalog.Payments,
            (visibility, cancellationToken) => BuildPaidAccountReportAsync(start, end, source, NormalizeOptional(accountName), NormalizeOptional(counterpartyName), clientId, caseId, contractId, incomeTypeId, visibility, cancellationToken),
            ct);
    }

    [HttpGet("paid-invoices")]
    public async Task<ActionResult<PaidInvoiceReportDto>> PaidInvoices(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] MoneyOperationSource? source,
        [FromQuery] string? accountName,
        [FromQuery] string? counterpartyName,
        [FromQuery] int? clientId,
        [FromQuery] int? caseId,
        [FromQuery] int? contractId,
        [FromQuery] int? incomeTypeId,
        CancellationToken ct)
    {
        var (start, end) = await ResolveReportPeriodAsync(from, to, ct);

        return await ExecuteVisibleReportAsync(
            RolePermissionCatalog.Payments,
            (visibility, cancellationToken) => BuildPaidInvoiceReportAsync(start, end, source, NormalizeOptional(accountName), NormalizeOptional(counterpartyName), clientId, caseId, contractId, incomeTypeId, visibility, cancellationToken),
            ct);
    }

    [HttpGet("invoice-statuses")]
    public async Task<ActionResult<InvoiceStatusReportDto>> InvoiceStatuses(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        CancellationToken ct)
    {
        var (start, end) = await ResolveReportPeriodAsync(from, to, ct);

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
        var (start, end) = await ResolveReportPeriodAsync(from, to, ct);

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
        var (start, end) = await ResolveReportPeriodAsync(from, to, ct);

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
        [FromQuery] string? accountName,
        [FromQuery] string? counterpartyName,
        [FromQuery] int? clientId,
        [FromQuery] int? caseId,
        [FromQuery] int? contractId,
        [FromQuery] int? incomeTypeId,
        CancellationToken ct)
    {
        var (start, end) = await ResolveReportPeriodAsync(from, to, ct);

        return await ExecuteVisibleReportAsync(
            RolePermissionCatalog.Payments,
            (visibility, cancellationToken) => BuildPaymentOrderReportAsync(start, end, source, NormalizeOptional(accountName), NormalizeOptional(counterpartyName), clientId, caseId, contractId, incomeTypeId, visibility, cancellationToken),
            ct);
    }

    [HttpGet("payment-purposes")]
    public async Task<ActionResult<PaymentPurposeReportDto>> PaymentPurposes(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] MoneyOperationSource? source,
        [FromQuery] string? accountName,
        [FromQuery] string? counterpartyName,
        [FromQuery] int? clientId,
        [FromQuery] int? caseId,
        [FromQuery] int? contractId,
        [FromQuery] int? incomeTypeId,
        CancellationToken ct)
    {
        var (start, end) = await ResolveReportPeriodAsync(from, to, ct);

        return await ExecuteVisibleReportAsync(
            RolePermissionCatalog.Payments,
            (visibility, cancellationToken) => BuildPaymentPurposeReportAsync(start, end, source, NormalizeOptional(accountName), NormalizeOptional(counterpartyName), clientId, caseId, contractId, incomeTypeId, visibility, cancellationToken),
            ct);
    }

    [HttpGet("match-statuses")]
    public async Task<ActionResult<MatchStatusReportDto>> MatchStatuses(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] MoneyOperationSource? source,
        [FromQuery] string? accountName,
        [FromQuery] string? counterpartyName,
        [FromQuery] int? clientId,
        [FromQuery] int? caseId,
        [FromQuery] int? contractId,
        [FromQuery] int? incomeTypeId,
        CancellationToken ct)
    {
        var (start, end) = await ResolveReportPeriodAsync(from, to, ct);

        return await ExecuteVisibleReportAsync(
            RolePermissionCatalog.Payments,
            (visibility, cancellationToken) => BuildMatchStatusReportAsync(start, end, source, NormalizeOptional(accountName), NormalizeOptional(counterpartyName), clientId, caseId, contractId, incomeTypeId, visibility, cancellationToken),
            ct);
    }

    [HttpGet("journal-statuses")]
    public async Task<ActionResult<JournalStatusReportDto>> JournalStatuses(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] MoneyOperationSource? source,
        [FromQuery] string? accountName,
        [FromQuery] string? counterpartyName,
        [FromQuery] int? clientId,
        [FromQuery] int? caseId,
        [FromQuery] int? contractId,
        [FromQuery] int? incomeTypeId,
        CancellationToken ct)
    {
        var (start, end) = await ResolveReportPeriodAsync(from, to, ct);

        return await ExecuteVisibleReportAsync(
            RolePermissionCatalog.Payments,
            (visibility, cancellationToken) => BuildJournalStatusReportAsync(start, end, source, NormalizeOptional(accountName), NormalizeOptional(counterpartyName), clientId, caseId, contractId, incomeTypeId, visibility, cancellationToken),
            ct);
    }

    [HttpGet("currencies")]
    public async Task<ActionResult<CurrencyReportDto>> Currencies(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] MoneyOperationSource? source,
        [FromQuery] string? accountName,
        [FromQuery] string? counterpartyName,
        [FromQuery] int? clientId,
        [FromQuery] int? caseId,
        [FromQuery] int? contractId,
        [FromQuery] int? incomeTypeId,
        CancellationToken ct)
    {
        var (start, end) = await ResolveReportPeriodAsync(from, to, ct);

        return await ExecuteVisibleReportAsync(
            RolePermissionCatalog.Payments,
            (visibility, cancellationToken) => BuildCurrencyReportAsync(start, end, source, NormalizeOptional(accountName), NormalizeOptional(counterpartyName), clientId, caseId, contractId, incomeTypeId, visibility, cancellationToken),
            ct);
    }

    [HttpGet("cash-orders")]
    public async Task<ActionResult<CashOrderReportDto>> CashOrders(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        CancellationToken ct)
    {
        var (start, end) = await ResolveReportPeriodAsync(from, to, ct);

        if (!await CanViewReportsAsync(ct))
        {
            return Forbid();
        }

        if (!await HasPaymentsPermissionAsync(static permission => permission.CanView, ct))
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
        var (start, end) = await ResolveReportPeriodAsync(from, to, ct);

        if (!await CanViewReportsAsync(ct))
        {
            return Forbid();
        }

        var report = await SqliteRetryHelper.ExecuteAsync(
            async cancellationToken =>
            {
                var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), cancellationToken);
                return await BuildBankStatementImportReportAsync(start, end, timeZone, cancellationToken);
            },
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
        var (start, end) = await ResolveReportPeriodAsync(from, to, ct);

        if (!await CanViewReportsAsync(ct))
        {
            return Forbid();
        }

        var report = await SqliteRetryHelper.ExecuteAsync(
            async cancellationToken =>
            {
                var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), cancellationToken);
                return await BuildSpreadsheetImportReportAsync(start, end, timeZone, cancellationToken);
            },
            _logger,
            ct);

        return Ok(report);
    }

    private async Task<LegalWorkReportDto> BuildLegalWorkReportAsync(
        DateTime start,
        DateTime end,
        DateTime today,
        TimeZoneInfo timeZone,
        LegalWorkReportFilters filters,
        DataVisibilityAccess casesVisibility,
        DataVisibilityAccess tasksVisibility,
        DataVisibilityAccess accountsVisibility,
        CancellationToken ct)
    {
        var endExclusiveUtc = CalendarTimeZoneHelper.FromUserLocalToUtc(end.Date.AddDays(1), timeZone);

        var caseQuery = _db.ClientCases
            .AsNoTracking()
            .WithCaseIncludes()
            .ApplyClientCaseVisibility(casesVisibility)
            .Where(item => item.CreatedAt < endExclusiveUtc
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

        var payments = await BuildLegalWorkPaymentRowsAsync(caseIds, accountsVisibility, PaymentType.Income, ct);
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
            From = NormalizeReportDate(start),
            To = NormalizeReportDate(end),
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
        TimeZoneInfo timeZone,
        LegalWorkReportFilters filters,
        int limit,
        DataVisibilityAccess casesVisibility,
        DataVisibilityAccess accountsVisibility,
        CancellationToken ct)
    {
        var endExclusiveUtc = CalendarTimeZoneHelper.FromUserLocalToUtc(end.Date.AddDays(1), timeZone);
        var caseQuery = _db.ClientCases
            .AsNoTracking()
            .WithCaseIncludes()
            .ApplyClientCaseVisibility(casesVisibility)
            .Where(item => item.CreatedAt < endExclusiveUtc
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
        var incomePayments = await BuildLegalWorkPaymentRowsAsync(caseIds, accountsVisibility, PaymentType.Income, ct);
        var expensePayments = await BuildLegalWorkPaymentRowsAsync(caseIds, accountsVisibility, PaymentType.Expense, ct);
        var paidByCase = incomePayments
            .GroupBy(item => item.CaseId)
            .ToDictionary(
                group => group.Key,
                group => group.Sum(item => ResolvePaidAmount(item.Amount, item.PaidAmount, item.Status)));
        var expenseByCase = expensePayments
            .GroupBy(item => item.CaseId)
            .ToDictionary(
                group => group.Key,
                group => group.Sum(item => ResolvePaidAmount(item.Amount, item.PaidAmount, item.Status)));

        var rows = cases
            .Select(item => BuildCaseFinanceItem(item, paidByCase.GetValueOrDefault(item.Id), expenseByCase.GetValueOrDefault(item.Id)))
            .OrderByDescending(item => item.OutstandingTotal)
            .ThenBy(item => item.DueDate ?? DateTime.MaxValue)
            .ThenBy(item => item.CaseTitle)
            .ToList();

        var feeTotal = rows.Sum(item => item.FeeAmount);
        var paidTotal = rows.Sum(item => item.PaidTotal);
        var expenseTotal = rows.Sum(item => item.ExpenseTotal);
        var outstandingTotal = rows.Sum(item => item.OutstandingTotal);
        var marginTotal = rows.Sum(item => item.MarginTotal);

        return new CaseFinanceReportDto
        {
            GeneratedAt = NormalizeReportInstant(DateTime.UtcNow),
            From = NormalizeReportDate(start),
            To = NormalizeReportDate(end),
            StatusFilter = filters.Status ?? string.Empty,
            ResponsibleUserIdFilter = filters.ResponsibleUserId,
            CaseTypeFilter = filters.CaseType ?? string.Empty,
            Limit = limit,
            TotalCases = rows.Count,
            ActiveCases = rows.Count(item => item.Status == nameof(ClientCaseStatus.Open) || item.Status == nameof(ClientCaseStatus.OnHold)),
            ClosedCases = rows.Count(item => item.Status == nameof(ClientCaseStatus.Closed) || item.Status == nameof(ClientCaseStatus.Lost)),
            FeeTotal = feeTotal,
            PaidTotal = paidTotal,
            ExpenseTotal = expenseTotal,
            OutstandingTotal = outstandingTotal,
            MarginTotal = marginTotal,
            CollectionRate = CalculatePercent(paidTotal, feeTotal),
            MarginRate = CalculatePercent(marginTotal, paidTotal),
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
                    var groupExpense = group.Sum(item => item.ExpenseTotal);
                    var groupMargin = group.Sum(item => item.MarginTotal);
                    return new CaseFinanceStatusItemDto
                    {
                        Status = group.Key.Status,
                        StatusLabel = group.Key.StatusLabel,
                        CaseCount = group.Count(),
                        FeeTotal = groupFee,
                        PaidTotal = groupPaid,
                        ExpenseTotal = groupExpense,
                        OutstandingTotal = group.Sum(item => item.OutstandingTotal),
                        MarginTotal = groupMargin,
                        CollectionRate = CalculatePercent(groupPaid, groupFee),
                        MarginRate = CalculatePercent(groupMargin, groupPaid),
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
                    var groupExpense = group.Sum(item => item.ExpenseTotal);
                    var groupMargin = group.Sum(item => item.MarginTotal);
                    return new CaseFinanceResponsibleItemDto
                    {
                        ResponsibleUserId = group.Key.ResponsibleUserId,
                        ResponsibleName = group.Key.ResponsibleName,
                        CaseCount = group.Count(),
                        FeeTotal = groupFee,
                        PaidTotal = groupPaid,
                        ExpenseTotal = groupExpense,
                        OutstandingTotal = group.Sum(item => item.OutstandingTotal),
                        MarginTotal = groupMargin,
                        CollectionRate = CalculatePercent(groupPaid, groupFee),
                        MarginRate = CalculatePercent(groupMargin, groupPaid),
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
        DateTime asOf,
        TimeZoneInfo timeZone,
        LegalWorkReportFilters filters,
        int limit,
        DataVisibilityAccess casesVisibility,
        CancellationToken ct)
    {
        var endExclusiveUtc = CalendarTimeZoneHelper.FromUserLocalToUtc(end.Date.AddDays(1), timeZone);
        var caseQuery = _db.ClientCases
            .AsNoTracking()
            .WithCaseIncludes()
            .ApplyClientCaseVisibility(casesVisibility)
            .Where(item => item.CreatedAt < endExclusiveUtc
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
            .Select(item => BuildCaseSuccessItem(item, asOf, timeZone))
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
            GeneratedAt = NormalizeReportInstant(DateTime.UtcNow),
            From = NormalizeReportDate(start),
            To = NormalizeReportDate(end),
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

    private static CaseSuccessItemDto BuildCaseSuccessItem(ClientCase clientCase, DateTime asOf, TimeZoneInfo timeZone)
    {
        var (outcomeCategory, outcomeLabel) = ResolveCaseSuccessOutcome(clientCase.Status);
        var start = clientCase.StartDate.HasValue
            ? NormalizeReportDate(clientCase.StartDate.Value)
            : ResolveReportLocalDate(clientCase.CreatedAt, timeZone);
        var finishAnchor = clientCase.Status is ClientCaseStatus.Closed or ClientCaseStatus.Lost
            ? NormalizeReportDate(clientCase.DueDate) ?? NormalizeReportDate(asOf)
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
            CreatedAt = NormalizeReportInstant(clientCase.CreatedAt),
            StartDate = NormalizeReportDate(clientCase.StartDate),
            DueDate = NormalizeReportDate(clientCase.DueDate),
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

    private static CaseFinanceItemDto BuildCaseFinanceItem(ClientCase clientCase, decimal paid, decimal expense)
    {
        var fee = Math.Max(0m, clientCase.FeeAmount);
        var outstanding = Math.Max(0m, fee - paid);
        var normalizedExpense = Math.Max(0m, expense);
        var margin = paid - normalizedExpense;
        var paymentRate = CalculatePercent(paid, fee);
        var marginRate = CalculatePercent(margin, paid);
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
            CreatedAt = NormalizeReportInstant(clientCase.CreatedAt),
            StartDate = NormalizeReportDate(clientCase.StartDate),
            DueDate = NormalizeReportDate(clientCase.DueDate),
            FeeAmount = fee,
            PaidTotal = paid,
            ExpenseTotal = normalizedExpense,
            OutstandingTotal = outstanding,
            MarginTotal = margin,
            PaymentRate = paymentRate,
            MarginRate = marginRate,
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
                    CreatedAt = NormalizeReportInstant(clientCase.CreatedAt),
                    StartDate = NormalizeReportDate(clientCase.StartDate),
                    DueDate = NormalizeReportDate(clientCase.DueDate),
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

    private static ReportExportDocument BuildCaseFinanceExportDocument(CaseFinanceReportDto report)
        => new(
            "case-finance-report",
            "Финансовый отчет по делам",
            $"Период: {DateCell(report.From)} - {DateCell(report.To)}",
            [
                new("Всего дел", report.TotalCases.ToString(CultureInfo.InvariantCulture)),
                new("В работе", report.ActiveCases.ToString(CultureInfo.InvariantCulture)),
                new("Закрыто", report.ClosedCases.ToString(CultureInfo.InvariantCulture)),
                new("Гонорар", MoneyCell(report.FeeTotal)),
                new("Оплачено", MoneyCell(report.PaidTotal)),
                new("Расходы", MoneyCell(report.ExpenseTotal)),
                new("Остаток", MoneyCell(report.OutstandingTotal)),
                new("Маржа", MoneyCell(report.MarginTotal)),
                new("Сбор оплат", PercentCell(report.CollectionRate)),
                new("Маржинальность", PercentCell(report.MarginRate)),
                new("Полностью оплачено", report.FullyPaidCaseCount.ToString(CultureInfo.InvariantCulture)),
            ],
            [
                new("Дела", ["Дело", "Клиент", "Ответственный", "Тип", "Статус", "Гонорар", "Оплачено", "Расходы", "Остаток", "Маржа", "Оплата", "Маржинальность", "Финансовое состояние"], report.Items.Select(item => new ReportExportRow([
                    BuildCaseTitle(item.CaseNumber, item.CaseTitle),
                    item.ClientName,
                    item.ResponsibleName,
                    item.CaseType,
                    item.StatusLabel,
                    MoneyCell(item.FeeAmount),
                    MoneyCell(item.PaidTotal),
                    MoneyCell(item.ExpenseTotal),
                    MoneyCell(item.OutstandingTotal),
                    MoneyCell(item.MarginTotal),
                    PercentCell(item.PaymentRate),
                    PercentCell(item.MarginRate),
                    item.FinanceStateLabel,
                ])).ToList()),
                new("По ответственным", ["Ответственный", "Дел", "Гонорар", "Оплачено", "Расходы", "Остаток", "Маржа", "Сбор оплат", "Маржинальность"], report.ByResponsible.Select(item => new ReportExportRow([
                    item.ResponsibleName,
                    item.CaseCount.ToString(CultureInfo.InvariantCulture),
                    MoneyCell(item.FeeTotal),
                    MoneyCell(item.PaidTotal),
                    MoneyCell(item.ExpenseTotal),
                    MoneyCell(item.OutstandingTotal),
                    MoneyCell(item.MarginTotal),
                    PercentCell(item.CollectionRate),
                    PercentCell(item.MarginRate),
                ])).ToList()),
                new("По статусам", ["Статус", "Дел", "Гонорар", "Оплачено", "Расходы", "Остаток", "Маржа", "Сбор оплат", "Маржинальность"], report.ByStatus.Select(item => new ReportExportRow([
                    item.StatusLabel,
                    item.CaseCount.ToString(CultureInfo.InvariantCulture),
                    MoneyCell(item.FeeTotal),
                    MoneyCell(item.PaidTotal),
                    MoneyCell(item.ExpenseTotal),
                    MoneyCell(item.OutstandingTotal),
                    MoneyCell(item.MarginTotal),
                    PercentCell(item.CollectionRate),
                    PercentCell(item.MarginRate),
                ])).ToList()),
            ]);

    private static ReportExportDocument BuildCashFlowExportDocument(CashFlowReportDto report)
        => new(
            "cash-flow-report",
            "Отчет движения денежных средств",
            $"Период: {DateCell(report.From)} - {DateCell(report.To)}{BuildCashFlowFilterSuffix(report)}",
            [
                new("Начальный остаток", MoneyCell(report.OpeningBalance)),
                new("Поступления", MoneyCell(report.Income)),
                new("Списания", MoneyCell(report.Expense)),
                new("Чистый поток", MoneyCell(report.NetCashFlow)),
                new("Конечный остаток", MoneyCell(report.ClosingBalance)),
                new("Фактические приходы", report.IncomeCount.ToString(CultureInfo.InvariantCulture)),
                new("Фактические расходы", report.ExpenseCount.ToString(CultureInfo.InvariantCulture)),
            ],
            [
                new("Динамика по дням", ["Дата", "Начальный остаток", "Поступления", "Списания", "Чистый поток", "Конечный остаток", "Приходов", "Расходов"], report.Days.Select(day => new ReportExportRow([
                    DateCell(day.Date),
                    MoneyCell(day.OpeningBalance),
                    MoneyCell(day.Income),
                    MoneyCell(day.Expense),
                    MoneyCell(day.NetCashFlow),
                    MoneyCell(day.ClosingBalance),
                    day.IncomeCount.ToString(CultureInfo.InvariantCulture),
                    day.ExpenseCount.ToString(CultureInfo.InvariantCulture),
                ])).ToList()),
                new("Счета и источники", ["Источник", "Счет", "Начальный остаток", "Поступления", "Списания", "Чистый поток", "Конечный остаток", "Приходов", "Расходов"], report.ByAccount.Select(account => new ReportExportRow([
                    MoneyOperationSourceCell(account.Source),
                    account.AccountName,
                    MoneyCell(account.OpeningBalance),
                    MoneyCell(account.Income),
                    MoneyCell(account.Expense),
                    MoneyCell(account.NetCashFlow),
                    MoneyCell(account.ClosingBalance),
                    account.IncomeCount.ToString(CultureInfo.InvariantCulture),
                    account.ExpenseCount.ToString(CultureInfo.InvariantCulture),
                ])).ToList()),
                new("Итоги по источникам", ["Источник", "Начальный остаток", "Поступления", "Списания", "Чистый поток", "Конечный остаток", "Приходов", "Расходов"], report.BySource.Select(source => new ReportExportRow([
                    MoneyOperationSourceCell(source.Source),
                    MoneyCell(source.OpeningBalance),
                    MoneyCell(source.Income),
                    MoneyCell(source.Expense),
                    MoneyCell(source.NetCashFlow),
                    MoneyCell(source.ClosingBalance),
                    source.IncomeCount.ToString(CultureInfo.InvariantCulture),
                    source.ExpenseCount.ToString(CultureInfo.InvariantCulture),
                ])).ToList()),
            ]);

    private static ReportExportDocument BuildCashBookExportDocument(CashBookResponseDto report)
        => new(
            "cash-book-report",
            "Отчет по кассе",
            $"Период: {DateCell(report.From)} - {DateCell(report.To)}{(string.IsNullOrWhiteSpace(report.AccountName) ? string.Empty : $" · Касса: {report.AccountName}")}",
            [
                new("Остаток на начало", MoneyCell(report.OpeningBalance)),
                new("Приход по кассе", MoneyCell(report.Income)),
                new("Расход по кассе", MoneyCell(report.Expense)),
                new("Остаток на конец", MoneyCell(report.ClosingBalance)),
                new("ПКО за период", report.IncomingCount.ToString(CultureInfo.InvariantCulture)),
                new("РКО за период", report.OutgoingCount.ToString(CultureInfo.InvariantCulture)),
            ],
            [
                new("Дневное движение кассы", ["Дата", "Остаток на начало", "Приход", "Расход", "Остаток на конец", "ПКО", "РКО"], report.Days.Select(day => new ReportExportRow([
                    DateCell(day.Date),
                    MoneyCell(day.OpeningBalance),
                    MoneyCell(day.Income),
                    MoneyCell(day.Expense),
                    MoneyCell(day.ClosingBalance),
                    day.IncomingCount.ToString(CultureInfo.InvariantCulture),
                    day.OutgoingCount.ToString(CultureInfo.InvariantCulture),
                ])).ToList()),
            ]);

    private static ReportExportDocument BuildBankExportDocument(CashFlowReportDto report)
        => new(
            "bank-report",
            "Отчет по банку",
            $"Период: {DateCell(report.From)} - {DateCell(report.To)} · Источник: Банк{BuildCashFlowFilterSuffix(report)}",
            [
                new("Начальный остаток", MoneyCell(report.OpeningBalance)),
                new("Поступления", MoneyCell(report.Income)),
                new("Списания", MoneyCell(report.Expense)),
                new("Чистый поток", MoneyCell(report.NetCashFlow)),
                new("Конечный остаток", MoneyCell(report.ClosingBalance)),
                new("Фактические приходы", report.IncomeCount.ToString(CultureInfo.InvariantCulture)),
                new("Фактические расходы", report.ExpenseCount.ToString(CultureInfo.InvariantCulture)),
            ],
            [
                new("Динамика по дням", ["Дата", "Начальный остаток", "Поступления", "Списания", "Чистый поток", "Конечный остаток", "Приходов", "Расходов"], report.Days.Select(day => new ReportExportRow([
                    DateCell(day.Date),
                    MoneyCell(day.OpeningBalance),
                    MoneyCell(day.Income),
                    MoneyCell(day.Expense),
                    MoneyCell(day.NetCashFlow),
                    MoneyCell(day.ClosingBalance),
                    day.IncomeCount.ToString(CultureInfo.InvariantCulture),
                    day.ExpenseCount.ToString(CultureInfo.InvariantCulture),
                ])).ToList()),
                new("Расчетные счета", ["Счет", "Начальный остаток", "Поступления", "Списания", "Чистый поток", "Конечный остаток", "Приходов", "Расходов"], report.ByAccount.Select(account => new ReportExportRow([
                    account.AccountName,
                    MoneyCell(account.OpeningBalance),
                    MoneyCell(account.Income),
                    MoneyCell(account.Expense),
                    MoneyCell(account.NetCashFlow),
                    MoneyCell(account.ClosingBalance),
                    account.IncomeCount.ToString(CultureInfo.InvariantCulture),
                    account.ExpenseCount.ToString(CultureInfo.InvariantCulture),
                ])).ToList()),
            ]);

    private static ReportExportDocument BuildPlanFactExportDocument(PlanFactReportDto report)
        => new(
            "plan-fact-report",
            "План-факт отчет",
            $"Период: {DateCell(report.From)} - {DateCell(report.To)}{(report.Source.HasValue ? $" · Источник: {MoneyOperationSourceCell(report.Source.Value)}" : string.Empty)}",
            [
                new("План доходов", MoneyCell(report.PlanIncome)),
                new("Факт доходов", MoneyCell(report.FactIncome)),
                new("Отклонение доходов", MoneyCell(report.IncomeVariance)),
                new("Выполнение доходов", PercentCell(report.IncomeCompletionPercent)),
                new("План расходов", MoneyCell(report.PlanExpense)),
                new("Факт расходов", MoneyCell(report.FactExpense)),
                new("Отклонение расходов", MoneyCell(report.ExpenseVariance)),
                new("Исполнение расходов", PercentCell(report.ExpenseExecutionPercent)),
                new("Итоговое отклонение", MoneyCell(report.NetVariance)),
            ],
            [
                new("Динамика по дням", ["Дата", "План доходов", "Факт доходов", "Отклонение доходов", "План расходов", "Факт расходов", "Отклонение расходов", "План итог", "Факт итог", "Итоговое отклонение"], report.Days.Select(day => new ReportExportRow([
                    DateCell(day.Date),
                    MoneyCell(day.PlanIncome),
                    MoneyCell(day.FactIncome),
                    MoneyCell(day.IncomeVariance),
                    MoneyCell(day.PlanExpense),
                    MoneyCell(day.FactExpense),
                    MoneyCell(day.ExpenseVariance),
                    MoneyCell(day.PlanNetTotal),
                    MoneyCell(day.FactNetTotal),
                    MoneyCell(day.NetVariance),
                ])).ToList()),
            ]);

    private static ReportExportDocument BuildAdvanceExportDocument(AdvanceReportDto report)
        => new(
            "advance-report",
            "Отчет по авансам",
            $"Период: {DateCell(report.From)} - {DateCell(report.To)}{(report.Source.HasValue ? $" · Источник: {MoneyOperationSourceCell(report.Source.Value)}" : string.Empty)}",
            [
                new("Полученные авансы", MoneyCell(report.IncomeTotal)),
                new("Выданные авансы", MoneyCell(report.ExpenseTotal)),
                new("Признано", MoneyCell(report.RecognizedTotal)),
                new("К признанию", MoneyCell(report.UnrecognizedTotal)),
                new("Нераспределенные авансы на балансе", MoneyCell(report.UnallocatedBalanceTotal)),
                new("Признанных операций", report.RecognizedCount.ToString(CultureInfo.InvariantCulture)),
                new("Непризнанных операций", report.UnrecognizedCount.ToString(CultureInfo.InvariantCulture)),
                new("Нераспределенных авансов на балансе", report.UnallocatedBalanceCount.ToString(CultureInfo.InvariantCulture)),
            ],
            [
                new("Контрагенты по авансам", ["Контрагент", "Получено", "Выдано", "Признано", "К признанию", "Нераспределено на балансе", "Приходов", "Расходов", "Признанных операций", "Непризнанных операций", "Нераспределенных авансов"], report.Counterparties.Select(item => new ReportExportRow([
                    item.CounterpartyName,
                    MoneyCell(item.IncomeTotal),
                    MoneyCell(item.ExpenseTotal),
                    MoneyCell(item.RecognizedTotal),
                    MoneyCell(item.UnrecognizedTotal),
                    MoneyCell(item.UnallocatedBalanceTotal),
                    item.IncomeCount.ToString(CultureInfo.InvariantCulture),
                    item.ExpenseCount.ToString(CultureInfo.InvariantCulture),
                    item.RecognizedCount.ToString(CultureInfo.InvariantCulture),
                    item.UnrecognizedCount.ToString(CultureInfo.InvariantCulture),
                    item.UnallocatedBalanceCount.ToString(CultureInfo.InvariantCulture),
                ])).ToList()),
            ]);

    private static ReportExportDocument BuildPaymentPriorityExportDocument(PaymentPriorityReportDto report)
        => new(
            "payment-priority-report",
            "Отчет по приоритетам платежей",
            $"Период: {DateCell(report.From)} - {DateCell(report.To)}{(report.Source.HasValue ? $" · Источник: {MoneyOperationSourceCell(report.Source.Value)}" : string.Empty)}",
            [
                new("Поступило", MoneyCell(report.IncomeTotal)),
                new("Списано", MoneyCell(report.ExpenseTotal)),
                new("Итог", MoneyCell(report.NetTotal)),
                new("Групп приоритета", report.Priorities.Count.ToString(CultureInfo.InvariantCulture)),
            ],
            [
                new("Приоритеты", ["Приоритет", "Поступления", "Списания", "Итог", "Оборот", "Приходов", "Расходов", "Доля оборота"], report.Priorities.Select(item => new ReportExportRow([
                    item.PriorityLabel,
                    MoneyCell(item.Income),
                    MoneyCell(item.Expense),
                    MoneyCell(item.NetTotal),
                    MoneyCell(item.TotalAmount),
                    item.IncomeCount.ToString(CultureInfo.InvariantCulture),
                    item.ExpenseCount.ToString(CultureInfo.InvariantCulture),
                    PercentCell(item.Share),
                ])).ToList()),
            ]);

    private static ReportExportDocument BuildPaidAccountExportDocument(PaidAccountReportDto report)
        => new(
            "paid-account-report",
            "Отчет по счетам оплаты",
            $"Период: {DateCell(report.From)} - {DateCell(report.To)}{(report.Source.HasValue ? $" · Источник: {MoneyOperationSourceCell(report.Source.Value)}" : string.Empty)}",
            [
                new("Поступило", MoneyCell(report.IncomeTotal)),
                new("Списано", MoneyCell(report.ExpenseTotal)),
                new("Итог", MoneyCell(report.NetTotal)),
                new("Счетов оплаты", report.Accounts.Count.ToString(CultureInfo.InvariantCulture)),
            ],
            [
                new("Счета оплаты", ["Счет оплаты", "Поступления", "Списания", "Итог", "Оборот", "Приходов", "Расходов", "Доля оборота"], report.Accounts.Select(item => new ReportExportRow([
                    item.PaidAccountName,
                    MoneyCell(item.Income),
                    MoneyCell(item.Expense),
                    MoneyCell(item.NetTotal),
                    MoneyCell(item.TotalAmount),
                    item.IncomeCount.ToString(CultureInfo.InvariantCulture),
                    item.ExpenseCount.ToString(CultureInfo.InvariantCulture),
                    PercentCell(item.Share),
                ])).ToList()),
            ]);

    private static ReportExportDocument BuildPaidInvoiceExportDocument(PaidInvoiceReportDto report)
        => new(
            "paid-invoice-report",
            "Отчет по оплаченным счетам",
            $"Период: {DateCell(report.From)} - {DateCell(report.To)}{(report.Source.HasValue ? $" · Источник: {MoneyOperationSourceCell(report.Source.Value)}" : string.Empty)}",
            [
                new("Поступило", MoneyCell(report.IncomeTotal)),
                new("Списано", MoneyCell(report.ExpenseTotal)),
                new("Итог", MoneyCell(report.NetTotal)),
                new("Оплачено по счетам", MoneyCell(report.PaidAmountTotal)),
                new("Счетов", report.Invoices.Count.ToString(CultureInfo.InvariantCulture)),
            ],
            [
                new("Оплаченные счета", ["Счет", "Поступления", "Списания", "Итог", "Оборот операций", "Оплачено", "Приходов", "Расходов", "Доля оборота"], report.Invoices.Select(item => new ReportExportRow([
                    item.PaidInvoiceNumber,
                    MoneyCell(item.Income),
                    MoneyCell(item.Expense),
                    MoneyCell(item.NetTotal),
                    MoneyCell(item.OperationTotal),
                    MoneyCell(item.PaidAmountTotal),
                    item.IncomeCount.ToString(CultureInfo.InvariantCulture),
                    item.ExpenseCount.ToString(CultureInfo.InvariantCulture),
                    PercentCell(item.Share),
                ])).ToList()),
            ]);

    private static ReportExportDocument BuildInvoiceStatusExportDocument(InvoiceStatusReportDto report)
        => new(
            "invoice-status-report",
            "Отчет по статусам счетов",
            $"Период: {DateCell(report.From)} - {DateCell(report.To)}",
            [
                new("Счетов", report.InvoiceCount.ToString(CultureInfo.InvariantCulture)),
                new("Входящих", report.IncomeCount.ToString(CultureInfo.InvariantCulture)),
                new("Исходящих", report.ExpenseCount.ToString(CultureInfo.InvariantCulture)),
                new("Оплачено счетов", report.PaidCount.ToString(CultureInfo.InvariantCulture)),
                new("Просрочено счетов", report.OverdueCount.ToString(CultureInfo.InvariantCulture)),
                new("Сумма счетов", MoneyCell(report.TotalAmount)),
                new("Оплачено", MoneyCell(report.PaidAmount)),
                new("Остаток", MoneyCell(report.OutstandingAmount)),
            ],
            [
                new("Статусы счетов", ["Статус", "Счетов", "Входящих", "Исходящих", "Оплачено счетов", "Просрочено", "Сумма", "Оплачено", "Остаток", "Доля"], report.Statuses.Select(item => new ReportExportRow([
                    item.StatusLabel,
                    item.InvoiceCount.ToString(CultureInfo.InvariantCulture),
                    item.IncomeCount.ToString(CultureInfo.InvariantCulture),
                    item.ExpenseCount.ToString(CultureInfo.InvariantCulture),
                    item.PaidCount.ToString(CultureInfo.InvariantCulture),
                    item.OverdueCount.ToString(CultureInfo.InvariantCulture),
                    MoneyCell(item.TotalAmount),
                    MoneyCell(item.PaidAmount),
                    MoneyCell(item.OutstandingAmount),
                    PercentCell(item.Share),
                ])).ToList()),
            ]);

    private static ReportExportDocument BuildActStatusExportDocument(ActStatusReportDto report)
        => new(
            "act-status-report",
            "Отчет по статусам актов",
            $"Период: {DateCell(report.From)} - {DateCell(report.To)}",
            [
                new("Актов", report.ActCount.ToString(CultureInfo.InvariantCulture)),
                new("Создано", report.CreatedCount.ToString(CultureInfo.InvariantCulture)),
                new("Передано", report.TransferredCount.ToString(CultureInfo.InvariantCulture)),
                new("Подписано", report.SignedCount.ToString(CultureInfo.InvariantCulture)),
                new("Расторгнуто", report.TerminatedCount.ToString(CultureInfo.InvariantCulture)),
                new("Сумма актов", MoneyCell(report.TotalAmount)),
                new("Подписано на сумму", MoneyCell(report.SignedAmount)),
                new("В работе", MoneyCell(report.PendingAmount)),
            ],
            [
                new("Статусы актов", ["Статус", "Актов", "Создано", "Передано", "Подписано", "Расторгнуто", "Сумма", "Подписано на сумму", "В работе", "Доля"], report.Statuses.Select(item => new ReportExportRow([
                    item.WorkflowStatusLabel,
                    item.ActCount.ToString(CultureInfo.InvariantCulture),
                    item.CreatedCount.ToString(CultureInfo.InvariantCulture),
                    item.TransferredCount.ToString(CultureInfo.InvariantCulture),
                    item.SignedCount.ToString(CultureInfo.InvariantCulture),
                    item.TerminatedCount.ToString(CultureInfo.InvariantCulture),
                    MoneyCell(item.TotalAmount),
                    MoneyCell(item.SignedAmount),
                    MoneyCell(item.PendingAmount),
                    PercentCell(item.Share),
                ])).ToList()),
            ]);

    private static ReportExportDocument BuildContractStatusExportDocument(ContractStatusReportDto report)
        => new(
            "contract-status-report",
            "Отчет по статусам договоров",
            $"Период: {DateCell(report.From)} - {DateCell(report.To)}",
            [
                new("Договоров", report.ContractCount.ToString(CultureInfo.InvariantCulture)),
                new("Черновиков", report.DraftCount.ToString(CultureInfo.InvariantCulture)),
                new("Не подписано", report.NotSignedCount.ToString(CultureInfo.InvariantCulture)),
                new("На подписании", report.OnSigningCount.ToString(CultureInfo.InvariantCulture)),
                new("Подписано", report.ConcludedCount.ToString(CultureInfo.InvariantCulture)),
                new("Расторгнуто", report.TerminatedCount.ToString(CultureInfo.InvariantCulture)),
                new("Аннулировано", report.CancelledCount.ToString(CultureInfo.InvariantCulture)),
                new("Бессрочно", report.OpenEndedCount.ToString(CultureInfo.InvariantCulture)),
                new("Сумма договоров", MoneyCell(report.TotalAmount)),
                new("Активно", MoneyCell(report.ActiveAmount)),
                new("Закрыто", MoneyCell(report.ClosedAmount)),
            ],
            [
                new("Статусы договоров", ["Статус", "Договоров", "Бессрочно", "Истекает в периоде", "Сумма", "Доля"], report.Statuses.Select(item => new ReportExportRow([
                    item.StatusLabel,
                    item.ContractCount.ToString(CultureInfo.InvariantCulture),
                    item.OpenEndedCount.ToString(CultureInfo.InvariantCulture),
                    item.ExpiringCount.ToString(CultureInfo.InvariantCulture),
                    MoneyCell(item.TotalAmount),
                    PercentCell(item.Share),
                ])).ToList()),
            ]);

    private static ReportExportDocument BuildCategoryExportDocument(MoneyCategoryReportDto report)
        => new(
            "money-category-report",
            "Отчет по статьям доходов и расходов",
            $"Период: {DateCell(report.From)} - {DateCell(report.To)}",
            [
                new("Доходы по статьям", MoneyCell(report.IncomeTotal)),
                new("Расходы по статьям", MoneyCell(report.ExpenseTotal)),
                new("Итог по статьям", MoneyCell(report.NetTotal)),
                new("Статей доходов", report.Income.Count.ToString(CultureInfo.InvariantCulture)),
                new("Статей расходов", report.Expense.Count.ToString(CultureInfo.InvariantCulture)),
            ],
            [
                new("Статьи доходов", ["Статья", "Сумма", "Операций", "Доля"], report.Income.Select(item => new ReportExportRow([
                    item.CategoryName,
                    MoneyCell(item.Amount),
                    item.Count.ToString(CultureInfo.InvariantCulture),
                    PercentCell(item.Share),
                ])).ToList()),
                new("Статьи расходов", ["Статья", "Сумма", "Операций", "Доля"], report.Expense.Select(item => new ReportExportRow([
                    item.CategoryName,
                    MoneyCell(item.Amount),
                    item.Count.ToString(CultureInfo.InvariantCulture),
                    PercentCell(item.Share),
                ])).ToList()),
            ]);

    private static ReportExportDocument BuildCounterpartyExportDocument(CounterpartyReportDto report)
        => new(
            "counterparty-report",
            "Отчет по контрагентам",
            $"Период: {DateCell(report.From)} - {DateCell(report.To)}{(report.Source.HasValue ? $" · Источник: {MoneyOperationSourceCell(report.Source.Value)}" : string.Empty)}",
            [
                new("Поступило", MoneyCell(report.IncomeTotal)),
                new("Списано", MoneyCell(report.ExpenseTotal)),
                new("Итог", MoneyCell(report.NetTotal)),
                new("Контрагентов", report.Counterparties.Count.ToString(CultureInfo.InvariantCulture)),
            ],
            [
                new("Контрагенты", ["Контрагент", "Поступило", "Списано", "Итог", "Оборот", "Приходов", "Расходов", "Доля оборота"], report.Counterparties.Select(item => new ReportExportRow([
                    item.CounterpartyName,
                    MoneyCell(item.Income),
                    MoneyCell(item.Expense),
                    MoneyCell(item.NetTotal),
                    MoneyCell(item.TotalAmount),
                    item.IncomeCount.ToString(CultureInfo.InvariantCulture),
                    item.ExpenseCount.ToString(CultureInfo.InvariantCulture),
                    PercentCell(item.Share),
                ])).ToList()),
            ]);

    private static ReportExportDocument BuildProjectExportDocument(ProjectReportDto report)
        => new(
            "project-report",
            "Отчет по проектам",
            $"Период: {DateCell(report.From)} - {DateCell(report.To)}{(report.Source.HasValue ? $" · Источник: {MoneyOperationSourceCell(report.Source.Value)}" : string.Empty)}",
            [
                new("Поступило", MoneyCell(report.IncomeTotal)),
                new("Списано", MoneyCell(report.ExpenseTotal)),
                new("Итог", MoneyCell(report.NetTotal)),
                new("Проектов", report.Projects.Count.ToString(CultureInfo.InvariantCulture)),
            ],
            [
                new("Проекты", ["Проект", "Поступило", "Списано", "Итог", "Оборот", "Приходов", "Расходов", "Доля оборота"], report.Projects.Select(item => new ReportExportRow([
                    item.ProjectName,
                    MoneyCell(item.Income),
                    MoneyCell(item.Expense),
                    MoneyCell(item.NetTotal),
                    MoneyCell(item.TotalAmount),
                    item.IncomeCount.ToString(CultureInfo.InvariantCulture),
                    item.ExpenseCount.ToString(CultureInfo.InvariantCulture),
                    PercentCell(item.Share),
                ])).ToList()),
            ]);

    private static ReportExportDocument BuildDirectionExportDocument(DirectionReportDto report)
        => new(
            "direction-report",
            "Отчет по направлениям",
            $"Период: {DateCell(report.From)} - {DateCell(report.To)}{(report.Source.HasValue ? $" · Источник: {MoneyOperationSourceCell(report.Source.Value)}" : string.Empty)}",
            [
                new("Поступило", MoneyCell(report.IncomeTotal)),
                new("Списано", MoneyCell(report.ExpenseTotal)),
                new("Итог", MoneyCell(report.NetTotal)),
                new("Направлений", report.Directions.Count.ToString(CultureInfo.InvariantCulture)),
            ],
            [
                new("Направления", ["Направление", "Поступило", "Списано", "Итог", "Оборот", "Приходов", "Расходов", "Доля оборота"], report.Directions.Select(item => new ReportExportRow([
                    item.DirectionName,
                    MoneyCell(item.Income),
                    MoneyCell(item.Expense),
                    MoneyCell(item.NetTotal),
                    MoneyCell(item.TotalAmount),
                    item.IncomeCount.ToString(CultureInfo.InvariantCulture),
                    item.ExpenseCount.ToString(CultureInfo.InvariantCulture),
                    PercentCell(item.Share),
                ])).ToList()),
            ]);

    private static ReportExportDocument BuildSalesPointExportDocument(SalesPointReportDto report)
        => new(
            "sales-point-report",
            "Отчет по точкам продаж",
            $"Период: {DateCell(report.From)} - {DateCell(report.To)}{(report.Source.HasValue ? $" · Источник: {MoneyOperationSourceCell(report.Source.Value)}" : string.Empty)}",
            [
                new("Поступило", MoneyCell(report.IncomeTotal)),
                new("Списано", MoneyCell(report.ExpenseTotal)),
                new("Итог", MoneyCell(report.NetTotal)),
                new("Точек продаж", report.SalesPoints.Count.ToString(CultureInfo.InvariantCulture)),
            ],
            [
                new("Точки продаж", ["Точка продаж", "Поступило", "Списано", "Итог", "Оборот", "Приходов", "Расходов", "Доля оборота"], report.SalesPoints.Select(item => new ReportExportRow([
                    item.SalesPointName,
                    MoneyCell(item.Income),
                    MoneyCell(item.Expense),
                    MoneyCell(item.NetTotal),
                    MoneyCell(item.TotalAmount),
                    item.IncomeCount.ToString(CultureInfo.InvariantCulture),
                    item.ExpenseCount.ToString(CultureInfo.InvariantCulture),
                    PercentCell(item.Share),
                ])).ToList()),
            ]);

    private static ReportExportDocument BuildVatExportDocument(VatReportDto report)
        => new(
            "vat-report",
            "Отчет по НДС",
            $"Период: {DateCell(report.From)} - {DateCell(report.To)}{(report.Source.HasValue ? $" · Источник: {MoneyOperationSourceCell(report.Source.Value)}" : string.Empty)}",
            [
                new("Поступления с НДС", MoneyCell(report.IncomeAmountTotal)),
                new("НДС с поступлений", MoneyCell(report.IncomeVatTotal)),
                new("Расходы с НДС", MoneyCell(report.ExpenseAmountTotal)),
                new("Входящий НДС", MoneyCell(report.ExpenseVatTotal)),
                new("Баланс НДС", MoneyCell(report.VatBalance)),
                new("Ставок НДС", report.Rates.Count.ToString(CultureInfo.InvariantCulture)),
            ],
            [
                new("Ставки НДС", ["Ставка НДС", "База доходов", "НДС доходов", "Доходы с НДС", "База расходов", "НДС расходов", "Расходы с НДС", "Итог НДС", "Приходов", "Расходов"], report.Rates.Select(item => new ReportExportRow([
                    item.VatRateLabel,
                    MoneyCell(item.IncomeBase),
                    MoneyCell(item.IncomeVat),
                    MoneyCell(item.IncomeGross),
                    MoneyCell(item.ExpenseBase),
                    MoneyCell(item.ExpenseVat),
                    MoneyCell(item.ExpenseGross),
                    MoneyCell(item.NetVat),
                    item.IncomeCount.ToString(CultureInfo.InvariantCulture),
                    item.ExpenseCount.ToString(CultureInfo.InvariantCulture),
                ])).ToList()),
            ]);

    private static ReportExportDocument BuildPaymentOrderExportDocument(PaymentOrderReportDto report)
        => new(
            "payment-order-report",
            "Отчет по платежным поручениям",
            $"Период: {DateCell(report.From)} - {DateCell(report.To)}{(report.Source.HasValue ? $" · Источник: {MoneyOperationSourceCell(report.Source.Value)}" : string.Empty)}",
            [
                new("Поступило", MoneyCell(report.IncomeTotal)),
                new("Списано", MoneyCell(report.ExpenseTotal)),
                new("Итог", MoneyCell(report.NetTotal)),
                new("Платежных поручений", report.Orders.Count.ToString(CultureInfo.InvariantCulture)),
            ],
            [
                new("Платежные поручения", ["Номер платежки", "Поступило", "Списано", "Итог", "Оборот", "Приходов", "Расходов", "Доля оборота"], report.Orders.Select(item => new ReportExportRow([
                    item.PaymentOrderNumber,
                    MoneyCell(item.Income),
                    MoneyCell(item.Expense),
                    MoneyCell(item.NetTotal),
                    MoneyCell(item.TotalAmount),
                    item.IncomeCount.ToString(CultureInfo.InvariantCulture),
                    item.ExpenseCount.ToString(CultureInfo.InvariantCulture),
                    PercentCell(item.Share),
                ])).ToList()),
            ]);

    private static ReportExportDocument BuildPaymentPurposeExportDocument(PaymentPurposeReportDto report)
        => new(
            "payment-purpose-report",
            "Отчет по назначениям платежей",
            $"Период: {DateCell(report.From)} - {DateCell(report.To)}{(report.Source.HasValue ? $" · Источник: {MoneyOperationSourceCell(report.Source.Value)}" : string.Empty)}",
            [
                new("Поступило", MoneyCell(report.IncomeTotal)),
                new("Списано", MoneyCell(report.ExpenseTotal)),
                new("Итог", MoneyCell(report.NetTotal)),
                new("Назначений платежей", report.Purposes.Count.ToString(CultureInfo.InvariantCulture)),
            ],
            [
                new("Назначения платежей", ["Назначение", "Поступило", "Списано", "Итог", "Оборот", "Приходов", "Расходов", "Доля оборота"], report.Purposes.Select(item => new ReportExportRow([
                    item.Purpose,
                    MoneyCell(item.Income),
                    MoneyCell(item.Expense),
                    MoneyCell(item.NetTotal),
                    MoneyCell(item.TotalAmount),
                    item.IncomeCount.ToString(CultureInfo.InvariantCulture),
                    item.ExpenseCount.ToString(CultureInfo.InvariantCulture),
                    PercentCell(item.Share),
                ])).ToList()),
            ]);

    private static ReportExportDocument BuildMatchStatusExportDocument(MatchStatusReportDto report)
        => new(
            "match-status-report",
            "Отчет по статусам сверки",
            $"Период: {DateCell(report.From)} - {DateCell(report.To)}{(report.Source.HasValue ? $" · Источник: {MoneyOperationSourceCell(report.Source.Value)}" : string.Empty)}",
            [
                new("Операций", report.OperationCount.ToString(CultureInfo.InvariantCulture)),
                new("Сверено", report.MatchedCount.ToString(CultureInfo.InvariantCulture)),
                new("Требуют внимания", report.NeedsAttentionCount.ToString(CultureInfo.InvariantCulture)),
                new("Поступило", MoneyCell(report.IncomeTotal)),
                new("Списано", MoneyCell(report.ExpenseTotal)),
                new("Итог", MoneyCell(report.NetTotal)),
            ],
            [
                new("Статусы сверки", ["Статус", "Поступило", "Списано", "Итог", "Оборот", "Операций", "Приходов", "Расходов", "Доля оборота"], report.Statuses.Select(item => new ReportExportRow([
                    item.StatusLabel,
                    MoneyCell(item.Income),
                    MoneyCell(item.Expense),
                    MoneyCell(item.NetTotal),
                    MoneyCell(item.TotalAmount),
                    item.OperationCount.ToString(CultureInfo.InvariantCulture),
                    item.IncomeCount.ToString(CultureInfo.InvariantCulture),
                    item.ExpenseCount.ToString(CultureInfo.InvariantCulture),
                    PercentCell(item.Share),
                ])).ToList()),
            ]);

    private static ReportExportDocument BuildJournalStatusExportDocument(JournalStatusReportDto report)
        => new(
            "journal-status-report",
            "Отчет по журнальным статусам",
            $"Период: {DateCell(report.From)} - {DateCell(report.To)}{(report.Source.HasValue ? $" · Источник: {MoneyOperationSourceCell(report.Source.Value)}" : string.Empty)}",
            [
                new("Операций", report.OperationCount.ToString(CultureInfo.InvariantCulture)),
                new("Проведено", report.PostedCount.ToString(CultureInfo.InvariantCulture)),
                new("В обработке", report.ProcessingCount.ToString(CultureInfo.InvariantCulture)),
                new("Ошибок", report.ErrorCount.ToString(CultureInfo.InvariantCulture)),
                new("Поступило", MoneyCell(report.IncomeTotal)),
                new("Списано", MoneyCell(report.ExpenseTotal)),
                new("Итог", MoneyCell(report.NetTotal)),
            ],
            [
                new("Журнальные статусы", ["Статус", "Поступило", "Списано", "Итог", "Оборот", "Операций", "Приходов", "Расходов", "Доля оборота"], report.Statuses.Select(item => new ReportExportRow([
                    item.StatusLabel,
                    MoneyCell(item.Income),
                    MoneyCell(item.Expense),
                    MoneyCell(item.NetTotal),
                    MoneyCell(item.TotalAmount),
                    item.OperationCount.ToString(CultureInfo.InvariantCulture),
                    item.IncomeCount.ToString(CultureInfo.InvariantCulture),
                    item.ExpenseCount.ToString(CultureInfo.InvariantCulture),
                    PercentCell(item.Share),
                ])).ToList()),
            ]);

    private static ReportExportDocument BuildCurrencyExportDocument(CurrencyReportDto report)
        => new(
            "currency-report",
            "Отчет по валютам операций",
            $"Период: {DateCell(report.From)} - {DateCell(report.To)}{(report.Source.HasValue ? $" · Источник: {MoneyOperationSourceCell(report.Source.Value)}" : string.Empty)}",
            [
                new("Поступило", MoneyCell(report.IncomeTotal)),
                new("Списано", MoneyCell(report.ExpenseTotal)),
                new("Итог", MoneyCell(report.NetTotal)),
                new("Валют", report.Currencies.Count.ToString(CultureInfo.InvariantCulture)),
            ],
            [
                new("Валюты", ["Валюта", "Поступило", "Списано", "Итог", "Оборот", "Приходов", "Расходов", "Доля оборота"], report.Currencies.Select(item => new ReportExportRow([
                    item.Currency,
                    MoneyCell(item.Income),
                    MoneyCell(item.Expense),
                    MoneyCell(item.NetTotal),
                    MoneyCell(item.TotalAmount),
                    item.IncomeCount.ToString(CultureInfo.InvariantCulture),
                    item.ExpenseCount.ToString(CultureInfo.InvariantCulture),
                    PercentCell(item.Share),
                ])).ToList()),
            ]);

    private static ReportExportDocument BuildCashOrderExportDocument(CashOrderReportDto report)
        => new(
            "cash-order-report",
            "Отчет по кассовым ордерам",
            $"Период: {DateCell(report.From)} - {DateCell(report.To)}",
            [
                new("Приход проведен", MoneyCell(report.PostedIncomingTotal)),
                new("Расход проведен", MoneyCell(report.PostedOutgoingTotal)),
                new("Итог проведенных ордеров", MoneyCell(report.PostedNetTotal)),
                new("Всего ордеров", report.OrderCount.ToString(CultureInfo.InvariantCulture)),
                new("Черновиков", report.DraftCount.ToString(CultureInfo.InvariantCulture)),
                new("Проведено", report.PostedCount.ToString(CultureInfo.InvariantCulture)),
                new("Отменено", report.CancelledCount.ToString(CultureInfo.InvariantCulture)),
            ],
            [
                new("Статусы кассовых ордеров", ["Статус", "Приход", "Расход", "Итог", "Оборот", "Ордеров", "ПКО", "РКО", "Доля оборота"], report.Statuses.Select(item => new ReportExportRow([
                    item.StatusLabel,
                    MoneyCell(item.IncomingTotal),
                    MoneyCell(item.OutgoingTotal),
                    MoneyCell(item.NetTotal),
                    MoneyCell(item.TotalAmount),
                    item.OrderCount.ToString(CultureInfo.InvariantCulture),
                    item.IncomingCount.ToString(CultureInfo.InvariantCulture),
                    item.OutgoingCount.ToString(CultureInfo.InvariantCulture),
                    PercentCell(item.Share),
                ])).ToList()),
            ]);

    private static ReportExportDocument BuildBankStatementImportExportDocument(BankStatementImportReportDto report)
        => new(
            "bank-statement-import-report",
            "Отчет по импортам банковских выписок",
            $"Период: {DateCell(report.From)} - {DateCell(report.To)}",
            [
                new("Импортов", report.BatchCount.ToString(CultureInfo.InvariantCulture)),
                new("Прочитано строк", report.RowsReadTotal.ToString(CultureInfo.InvariantCulture)),
                new("Создано операций", report.CreatedRowsTotal.ToString(CultureInfo.InvariantCulture)),
                new("Пропущено строк", report.SkippedRowsTotal.ToString(CultureInfo.InvariantCulture)),
                new("Ошибок", report.ErrorRowsTotal.ToString(CultureInfo.InvariantCulture)),
                new("Строк отката", report.RollbackRowsTotal.ToString(CultureInfo.InvariantCulture)),
                new("Применено", report.AppliedCount.ToString(CultureInfo.InvariantCulture)),
                new("Применено с ошибками", report.AppliedWithErrorsCount.ToString(CultureInfo.InvariantCulture)),
                new("Откатано", report.RolledBackCount.ToString(CultureInfo.InvariantCulture)),
                new("Откат заблокирован", report.RollbackBlockedCount.ToString(CultureInfo.InvariantCulture)),
            ],
            [
                new("Статусы импортов", ["Статус", "Импортов", "Прочитано строк", "Создано", "Пропущено", "Ошибки", "Откатано строк", "Доля импортов"], report.Statuses.Select(item => new ReportExportRow([
                    item.StatusLabel,
                    item.BatchCount.ToString(CultureInfo.InvariantCulture),
                    item.RowsRead.ToString(CultureInfo.InvariantCulture),
                    item.CreatedRows.ToString(CultureInfo.InvariantCulture),
                    item.SkippedRows.ToString(CultureInfo.InvariantCulture),
                    item.ErrorRows.ToString(CultureInfo.InvariantCulture),
                    item.RollbackRows.ToString(CultureInfo.InvariantCulture),
                    PercentCell(item.Share),
                ])).ToList()),
            ]);

    private static ReportExportDocument BuildSpreadsheetImportExportDocument(SpreadsheetImportReportDto report)
        => new(
            "spreadsheet-import-report",
            "Отчет по импортам из таблиц",
            $"Период: {DateCell(report.From)} - {DateCell(report.To)}",
            [
                new("Пакетов", report.BatchCount.ToString(CultureInfo.InvariantCulture)),
                new("Файлов", report.FileCount.ToString(CultureInfo.InvariantCulture)),
                new("Строк", report.TotalRows.ToString(CultureInfo.InvariantCulture)),
                new("Создано", report.CreatedRows.ToString(CultureInfo.InvariantCulture)),
                new("Обновлено", report.UpdatedRows.ToString(CultureInfo.InvariantCulture)),
                new("Пропущено", report.SkippedRows.ToString(CultureInfo.InvariantCulture)),
                new("Ошибок", report.ErrorRows.ToString(CultureInfo.InvariantCulture)),
                new("Строк отката", report.RollbackRows.ToString(CultureInfo.InvariantCulture)),
                new("Применено", report.AppliedCount.ToString(CultureInfo.InvariantCulture)),
                new("Применено с ошибками", report.AppliedWithErrorsCount.ToString(CultureInfo.InvariantCulture)),
                new("Откатано", report.RolledBackCount.ToString(CultureInfo.InvariantCulture)),
                new("Откатано с конфликтами", report.RolledBackWithConflictsCount.ToString(CultureInfo.InvariantCulture)),
            ],
            [
                new("Статусы импортов", ["Статус", "Пакетов", "Файлов", "Строк", "Создано", "Обновлено", "Пропущено", "Ошибки", "Откатано строк", "Доля пакетов"], report.Statuses.Select(item => new ReportExportRow([
                    item.StatusLabel,
                    item.BatchCount.ToString(CultureInfo.InvariantCulture),
                    item.FileCount.ToString(CultureInfo.InvariantCulture),
                    item.TotalRows.ToString(CultureInfo.InvariantCulture),
                    item.CreatedRows.ToString(CultureInfo.InvariantCulture),
                    item.UpdatedRows.ToString(CultureInfo.InvariantCulture),
                    item.SkippedRows.ToString(CultureInfo.InvariantCulture),
                    item.ErrorRows.ToString(CultureInfo.InvariantCulture),
                    item.RollbackRows.ToString(CultureInfo.InvariantCulture),
                    PercentCell(item.Share),
                ])).ToList()),
            ]);

    private static ReportExportDocument BuildCaseSuccessExportDocument(CaseSuccessReportDto report)
        => new(
            "case-success-report",
            "Статистика успешности дел",
            $"Период: {DateCell(report.From)} - {DateCell(report.To)}. {report.FormulaNote}",
            [
                new("Всего дел", report.TotalCases.ToString(CultureInfo.InvariantCulture)),
                new("Завершено", report.CompletedCases.ToString(CultureInfo.InvariantCulture)),
                new("Успешно", report.SuccessfulCases.ToString(CultureInfo.InvariantCulture)),
                new("Отрицательный исход", report.LostCases.ToString(CultureInfo.InvariantCulture)),
                new("В работе", report.InProgressCases.ToString(CultureInfo.InvariantCulture)),
                new("На паузе", report.OnHoldCases.ToString(CultureInfo.InvariantCulture)),
                new("Успешность", PercentCell(report.SuccessRate)),
                new("Доля завершенных", PercentCell(report.CompletionRate)),
            ],
            [
                new("Дела", ["Дело", "Клиент", "Ответственный", "Тип", "Статус", "Исход", "Дней в работе", "Дата начала", "Срок дела"], report.Items.Select(item => new ReportExportRow([
                    BuildCaseTitle(item.CaseNumber, item.CaseTitle),
                    item.ClientName,
                    item.ResponsibleName,
                    item.CaseType,
                    item.StatusLabel,
                    item.OutcomeLabel,
                    item.DaysInWork.ToString(CultureInfo.InvariantCulture),
                    DateCell(item.StartDate),
                    DateCell(item.DueDate),
                ])).ToList()),
                new("По ответственным", ["Ответственный", "Всего", "Завершено", "Успешно", "Отрицательный исход", "В работе", "Успешность"], report.ByResponsible.Select(item => new ReportExportRow([
                    item.ResponsibleName,
                    item.TotalCases.ToString(CultureInfo.InvariantCulture),
                    item.CompletedCases.ToString(CultureInfo.InvariantCulture),
                    item.SuccessfulCases.ToString(CultureInfo.InvariantCulture),
                    item.LostCases.ToString(CultureInfo.InvariantCulture),
                    item.InProgressCases.ToString(CultureInfo.InvariantCulture),
                    PercentCell(item.SuccessRate),
                ])).ToList()),
                new("По типам дел", ["Тип", "Всего", "Завершено", "Успешно", "Отрицательный исход", "Успешность"], report.ByType.Select(item => new ReportExportRow([
                    item.CaseType,
                    item.TotalCases.ToString(CultureInfo.InvariantCulture),
                    item.CompletedCases.ToString(CultureInfo.InvariantCulture),
                    item.SuccessfulCases.ToString(CultureInfo.InvariantCulture),
                    item.LostCases.ToString(CultureInfo.InvariantCulture),
                    PercentCell(item.SuccessRate),
                ])).ToList()),
            ]);

    private static ReportExportDocument BuildCurrentCasesExportDocument(CurrentCasesReportDto report)
        => new(
            "current-cases-report",
            "Отчет по текущим делам",
            $"Дата среза: {DateCell(report.AsOf)}",
            [
                new("Текущие дела", report.TotalCurrentCases.ToString(CultureInfo.InvariantCulture)),
                new("Открыто", report.OpenCases.ToString(CultureInfo.InvariantCulture)),
                new("На паузе", report.OnHoldCases.ToString(CultureInfo.InvariantCulture)),
                new("Просрочено", report.OverdueCases.ToString(CultureInfo.InvariantCulture)),
                new("Без ответственного", report.WithoutResponsibleCount.ToString(CultureInfo.InvariantCulture)),
                new("С открытыми задачами", report.WithOpenTasksCount.ToString(CultureInfo.InvariantCulture)),
                new("С просроченными задачами", report.WithOverdueTasksCount.ToString(CultureInfo.InvariantCulture)),
                new("С просроченными сроками", report.WithOverdueDeadlinesCount.ToString(CultureInfo.InvariantCulture)),
            ],
            [
                new("Дела", ["Дело", "Клиент", "Ответственный", "Тип", "Статус", "Дней в работе", "Срок дела", "Просрочено", "Открытые задачи", "Просроченные задачи", "Активные сроки", "Ближайший срок"], report.Items.Select(item => new ReportExportRow([
                    BuildCaseTitle(item.CaseNumber, item.CaseTitle),
                    item.ClientName,
                    item.ResponsibleName,
                    item.CaseType,
                    item.StatusLabel,
                    item.DaysInWork.ToString(CultureInfo.InvariantCulture),
                    DateCell(item.DueDate),
                    item.IsOverdue ? "Да" : "Нет",
                    item.OpenTaskCount.ToString(CultureInfo.InvariantCulture),
                    item.OverdueTaskCount.ToString(CultureInfo.InvariantCulture),
                    item.ActiveDeadlineCount.ToString(CultureInfo.InvariantCulture),
                    item.NextDeadlineLabel,
                ])).ToList()),
                new("По ответственным", ["Ответственный", "Дел", "Просрочено дел", "Открытые задачи", "Просроченные задачи", "Просроченные сроки"], report.ByResponsible.Select(item => new ReportExportRow([
                    item.ResponsibleName,
                    item.CaseCount.ToString(CultureInfo.InvariantCulture),
                    item.OverdueCases.ToString(CultureInfo.InvariantCulture),
                    item.OpenTasks.ToString(CultureInfo.InvariantCulture),
                    item.OverdueTasks.ToString(CultureInfo.InvariantCulture),
                    item.OverdueDeadlines.ToString(CultureInfo.InvariantCulture),
                ])).ToList()),
            ]);

    private static ReportExportDocument BuildLawyerWorkloadExportDocument(LawyerWorkloadReportDto report, TimeZoneInfo timeZone)
        => new(
            "lawyer-workload-report",
            "Отчет по загрузке юристов",
            $"Дата среза: {DateCell(report.AsOf)}. Горизонт: {report.HorizonDays} дн.",
            [
                new("Юристов", report.LawyerCount.ToString(CultureInfo.InvariantCulture)),
                new("Активные дела", report.TotalActiveCases.ToString(CultureInfo.InvariantCulture)),
                new("Открытые задачи", report.TotalOpenTasks.ToString(CultureInfo.InvariantCulture)),
                new("Просроченные задачи", report.TotalOverdueTasks.ToString(CultureInfo.InvariantCulture)),
                new("Просроченные сроки", report.TotalOverdueDeadlines.ToString(CultureInfo.InvariantCulture)),
                new("Перегружено", report.OverloadedLawyerCount.ToString(CultureInfo.InvariantCulture)),
                new("Плановые часы", report.TotalPlannedHours.ToString("0.##", CultureInfo.InvariantCulture)),
            ],
            [
                new("Юристы", ["Юрист", "Активные дела", "Открытые задачи", "Просроченные задачи", "На согласовании", "Сегодня", "Ближайшие дни", "Просроченные сроки", "Активные сроки", "Плановые часы", "Индекс нагрузки", "Риск", "Ближайшая задача", "Ближайший срок"], report.Items.Select(item => new ReportExportRow([
                    item.LawyerName,
                    item.ActiveCaseCount.ToString(CultureInfo.InvariantCulture),
                    item.OpenTaskCount.ToString(CultureInfo.InvariantCulture),
                    item.OverdueTaskCount.ToString(CultureInfo.InvariantCulture),
                    item.OnApprovalTaskCount.ToString(CultureInfo.InvariantCulture),
                    item.DueTodayTaskCount.ToString(CultureInfo.InvariantCulture),
                    item.DueSoonTaskCount.ToString(CultureInfo.InvariantCulture),
                    item.OverdueDeadlineCount.ToString(CultureInfo.InvariantCulture),
                    item.ActiveDeadlineCount.ToString(CultureInfo.InvariantCulture),
                    item.PlannedHours.ToString("0.##", CultureInfo.InvariantCulture),
                    item.WorkloadScore.ToString(CultureInfo.InvariantCulture),
                    item.RiskLabel,
                    DateTimeCell(item.NearestTaskDueAt, timeZone),
                    item.NearestDeadlineAllDay
                        ? DateCell(item.NearestDeadlineAt)
                        : DateTimeCell(item.NearestDeadlineAt, timeZone),
                ])).ToList()),
            ]);

    private static ReportExportDocument BuildOverdueDeadlinesExportDocument(OverdueProceduralDeadlinesReportDto report, TimeZoneInfo timeZone)
        => new(
            "overdue-deadlines-report",
            "Отчет по просроченным процессуальным срокам",
            $"Дата среза: {DateCell(report.AsOf)}",
            [
                new("Просрочено сроков", report.TotalOverdueCount.ToString(CultureInfo.InvariantCulture)),
                new("Критичные", report.CriticalOverdueCount.ToString(CultureInfo.InvariantCulture)),
                new("Без ответственного", report.WithoutResponsibleCount.ToString(CultureInfo.InvariantCulture)),
            ],
            [
                new("Сроки", ["Дело", "Клиент", "Ответственный", "Тип срока", "Срок", "Дата", "Дней просрочки", "Статус", "Задача", "Документ"], report.Items.Select(item => new ReportExportRow([
                    BuildCaseTitle(item.CaseNumber, item.CaseTitle),
                    item.ClientName,
                    item.ResponsibleName,
                    item.DeadlineTypeLabel,
                    item.Title,
                    item.AllDay
                        ? DateCell(item.DueAt)
                        : DateTimeCell(item.DueAt, timeZone),
                    item.DaysOverdue.ToString(CultureInfo.InvariantCulture),
                    item.StatusLabel,
                    item.TaskTitle,
                    item.DocumentName,
                ])).ToList()),
                new("По типам сроков", ["Тип", "Всего", "Критичные"], report.ByType.Select(item => new ReportExportRow([
                    item.DeadlineTypeLabel,
                    item.Count.ToString(CultureInfo.InvariantCulture),
                    item.CriticalCount.ToString(CultureInfo.InvariantCulture),
                ])).ToList()),
                new("По ответственным", ["Ответственный", "Всего", "Критичные"], report.ByResponsible.Select(item => new ReportExportRow([
                    item.ResponsibleName,
                    item.Count.ToString(CultureInfo.InvariantCulture),
                    item.CriticalCount.ToString(CultureInfo.InvariantCulture),
                ])).ToList()),
            ]);

    private static ReportExportDocument BuildCaseActivityExportDocument(CaseActivityReportDto report, TimeZoneInfo timeZone)
        => new(
            "case-activity-report",
            "Отчет по активности дел",
            $"Порог неактивности: {report.InactivityDays} дн. Эскалация: {report.EscalationDays} дн.",
            [
                new("Видимые дела", report.TotalVisibleCases.ToString(CultureInfo.InvariantCulture)),
                new("Активные", report.ActiveCaseCount.ToString(CultureInfo.InvariantCulture)),
                new("Завершенные", report.FinishedCaseCount.ToString(CultureInfo.InvariantCulture)),
                new("Неактивные", report.InactiveCaseCount.ToString(CultureInfo.InvariantCulture)),
                new("Эскалации", report.EscalatedCaseCount.ToString(CultureInfo.InvariantCulture)),
                new("Контроль отключен", report.DisabledCaseCount.ToString(CultureInfo.InvariantCulture)),
                new("С активными задачами", report.WithActiveTasksCount.ToString(CultureInfo.InvariantCulture)),
                new("Без ответственного", report.WithoutResponsibleCount.ToString(CultureInfo.InvariantCulture)),
            ],
            [
                new("Дела", ["Дело", "Клиент", "Ответственный", "Тип", "Статус", "Последняя активность", "Дней без активности", "Активные задачи", "Завершенные задачи", "Неактивно", "Эскалация", "Причина"], report.Items.Select(item => new ReportExportRow([
                    BuildCaseTitle(item.CaseNumber, item.CaseTitle),
                    item.OwnerName,
                    item.ResponsibleName,
                    item.CaseType,
                    item.StatusLabel,
                    DateTimeCell(item.LastActivityAt, timeZone),
                    item.DaysWithoutActivity.ToString(CultureInfo.InvariantCulture),
                    item.ActiveTaskCount.ToString(CultureInfo.InvariantCulture),
                    item.CompletedTaskCount.ToString(CultureInfo.InvariantCulture),
                    item.IsInactive ? "Да" : "Нет",
                    item.IsEscalated ? "Да" : "Нет",
                    item.Reason,
                ])).ToList()),
            ]);

    private static ReportExportDocument BuildCashFlowForecastExportDocument(CashFlowForecastReportDto report)
        => new(
            "cash-flow-forecast-report",
            "Прогноз денежных потоков",
            $"Период: {DateCell(report.From)} - {DateCell(report.To)}{(string.IsNullOrWhiteSpace(report.AccountName) ? string.Empty : $" · Счет: {report.AccountName}")}",
            [
                new("Стартовый остаток", MoneyCell(report.OpeningBalance)),
                new("Ожидаемые поступления", MoneyCell(report.ExpectedIncome)),
                new("Ожидаемые списания", MoneyCell(report.ExpectedExpense)),
                new("Чистый прогноз", MoneyCell(report.NetProjectedFlow)),
                new("Прогнозный остаток", MoneyCell(report.ProjectedClosingBalance)),
                new("Незакрытые приходы", report.IncomeCount.ToString(CultureInfo.InvariantCulture)),
                new("Незакрытые расходы", report.ExpenseCount.ToString(CultureInfo.InvariantCulture)),
            ],
            [
                new("Ожидаемое движение по дням", ["Дата", "Ожидаемые поступления", "Ожидаемые списания", "Чистый поток", "Прогнозный остаток", "Приходов", "Расходов"], report.Days.Select(day => new ReportExportRow([
                    DateCell(day.Date),
                    MoneyCell(day.ExpectedIncome),
                    MoneyCell(day.ExpectedExpense),
                    MoneyCell(day.NetProjectedFlow),
                    MoneyCell(day.ProjectedClosingBalance),
                    day.IncomeCount.ToString(CultureInfo.InvariantCulture),
                    day.ExpenseCount.ToString(CultureInfo.InvariantCulture),
                ])).ToList()),
            ]);

    private static string BuildReportExportExcelXml(
        ReportExportDocument document,
        DateTime generatedAtUtc,
        TimeZoneInfo timeZone)
    {
        var builder = new StringBuilder();
        builder.AppendLine("""<?xml version="1.0" encoding="utf-8"?>""");
        builder.AppendLine("""<?mso-application progid="Excel.Sheet"?>""");
        builder.AppendLine("""
<Workbook xmlns="urn:schemas-microsoft-com:office:spreadsheet"
          xmlns:ss="urn:schemas-microsoft-com:office:spreadsheet">
  <Worksheet ss:Name="Report">
    <Table>
""");

        AppendLegalWorkExcelRow(builder, [document.Title], header: true);
        AppendLegalWorkExcelRow(builder, [document.Subtitle]);
        AppendLegalWorkExcelRow(builder, ["Сформировано", GeneratedAtCell(generatedAtUtc, timeZone)]);
        AppendLegalWorkExcelRow(builder, []);
        foreach (var metric in document.Metrics)
        {
            AppendLegalWorkExcelRow(builder, [metric.Label, metric.Value]);
        }

        foreach (var table in document.Tables)
        {
            AppendLegalWorkExcelRow(builder, []);
            AppendLegalWorkExcelRow(builder, [table.Title], header: true);
            AppendLegalWorkExcelRow(builder, table.Headers, header: true);
            foreach (var row in table.Rows)
            {
                AppendLegalWorkExcelRow(builder, row.Cells);
            }
        }

        builder.AppendLine("""
    </Table>
  </Worksheet>
</Workbook>
""");
        return builder.ToString();
    }

    private static string BuildReportExportPrintHtml(
        ReportExportDocument document,
        DateTime generatedAtUtc,
        TimeZoneInfo timeZone)
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
    h2 { margin: 20px 0 8px; font-size: 15px; }
    .muted { color: #6b7280; }
    .summary { display: grid; grid-template-columns: repeat(4, 1fr); gap: 8px; margin: 16px 0; }
    .metric { border: 1px solid #e5e7eb; border-radius: 8px; padding: 8px; }
    .metric strong { display: block; font-size: 16px; margin-top: 4px; }
    table { width: 100%; border-collapse: collapse; margin-top: 8px; }
    th, td { border: 1px solid #e5e7eb; padding: 5px 6px; vertical-align: top; }
    th { background: #f3f4f6; text-align: left; }
  </style>
</head>
<body>
""");
        builder.Append("<h1>").Append(Html(document.Title)).AppendLine("</h1>");
        builder.Append("<div class=\"muted\">")
            .Append(Html(document.Subtitle))
            .Append(" · Сформировано: ")
            .Append(Html(GeneratedAtCell(generatedAtUtc, timeZone)))
            .AppendLine("</div>");

        builder.AppendLine("<div class=\"summary\">");
        foreach (var metric in document.Metrics)
        {
            AppendLegalWorkMetric(builder, metric.Label, metric.Value);
        }

        builder.AppendLine("</div>");
        foreach (var table in document.Tables)
        {
            builder.Append("<h2>").Append(Html(table.Title)).AppendLine("</h2><table><thead><tr>");
            foreach (var header in table.Headers)
            {
                builder.Append("<th>").Append(Html(header)).Append("</th>");
            }

            builder.AppendLine("</tr></thead><tbody>");
            foreach (var row in table.Rows)
            {
                builder.Append("<tr>");
                foreach (var cell in row.Cells)
                {
                    builder.Append("<td>").Append(Html(cell)).Append("</td>");
                }

                builder.AppendLine("</tr>");
            }

            builder.AppendLine("</tbody></table>");
        }

        builder.AppendLine("</body></html>");
        return builder.ToString();
    }

    private static byte[] BuildLegalWorkCsv(
        LegalWorkReportDto report,
        DateTime generatedAtUtc,
        TimeZoneInfo timeZone)
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
            "Сформировано",
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            GeneratedAtCell(generatedAtUtc, timeZone),
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty
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
                DateCell(item.CreatedAt, timeZone),
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

    private static string BuildLegalWorkExcelXml(
        LegalWorkReportDto report,
        DateTime generatedAtUtc,
        TimeZoneInfo timeZone)
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
        AppendLegalWorkExcelRow(builder, ["Сформировано", GeneratedAtCell(generatedAtUtc, timeZone)]);
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
                DateCell(item.CreatedAt, timeZone),
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
                LegalWorkDeadlineDateCell(item, timeZone),
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

    private static string BuildLegalWorkPrintHtml(
        LegalWorkReportDto report,
        DateTime generatedAtUtc,
        TimeZoneInfo timeZone)
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
            .Append(Html(GeneratedAtCell(generatedAtUtc, timeZone)))
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
                .Append("</td><td>").Append(Html(LegalWorkDeadlineDateCell(item, timeZone)))
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

    private static string BuildCaseTitle(string? caseNumber, string? caseTitle)
        => string.IsNullOrWhiteSpace(caseNumber)
            ? NormalizeReportText(caseTitle, "Без номера")
            : $"{caseNumber} {NormalizeReportText(caseTitle, "Без названия")}";

    private static string MoneyCell(decimal value)
        => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string MoneyOperationSourceCell(MoneyOperationSource source)
        => source switch
        {
            MoneyOperationSource.Bank => "Банк",
            MoneyOperationSource.Cash => "Касса",
            MoneyOperationSource.Card => "Карты",
            MoneyOperationSource.EMoney => "E-money",
            _ => source.ToString(),
        };

    private static string BuildCashFlowFilterSuffix(CashFlowReportDto report)
    {
        var parts = new List<string>();
        if (report.Source.HasValue)
        {
            parts.Add($"Источник: {MoneyOperationSourceCell(report.Source.Value)}");
        }

        if (!string.IsNullOrWhiteSpace(report.AccountName))
        {
            parts.Add($"Счет: {report.AccountName}");
        }

        return parts.Count == 0 ? string.Empty : $" · {string.Join(" · ", parts)}";
    }

    private static string PercentCell(decimal value)
        => $"{value.ToString("0.##", CultureInfo.InvariantCulture)}%";

    private static string GeneratedAtCell(DateTime generatedAtUtc, TimeZoneInfo timeZone)
        => CalendarTimeZoneHelper.FormatUserDateTime(generatedAtUtc, timeZone, culture: CultureInfo.GetCultureInfo("ru-RU"));

    private static string DateCell(DateTime? value, TimeZoneInfo? timeZone = null)
        => value.HasValue
            ? (timeZone is null
                ? value.Value
                : CalendarTimeZoneHelper.ToUserLocal(value.Value, timeZone))
                .ToString("dd.MM.yyyy", CultureInfo.GetCultureInfo("ru-RU"))
            : string.Empty;

    private static string LegalWorkDeadlineDateCell(LegalWorkDeadlineItemDto item, TimeZoneInfo timeZone)
        => string.Equals(item.DeadlineType, "hearing", StringComparison.OrdinalIgnoreCase)
            ? DateCell(item.DueAt, timeZone)
            : DateCell(item.DueAt);

    private static string DateTimeCell(DateTime? value, TimeZoneInfo? timeZone = null)
        => value.HasValue
            ? (timeZone is null
                ? value.Value
                : CalendarTimeZoneHelper.ToUserLocal(value.Value, timeZone))
                .ToString("dd.MM.yyyy HH:mm", CultureInfo.GetCultureInfo("ru-RU"))
            : string.Empty;

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
        PaymentType paymentType,
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
            .Where(item => item.Type == paymentType && item.Status != PaymentStatus.Cancelled);

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
        var commentRows = caseIds.Length == 0
            ? new List<CaseActivitySignalRow>()
            : await _db.TaskJournalComments
                .AsNoTracking()
                .Where(item => item.TaskJournalItem.ClientCaseId.HasValue && caseIds.Contains(item.TaskJournalItem.ClientCaseId.Value))
                .Select(item => new CaseActivitySignalRow(
                    item.TaskJournalItem.ClientCaseId!.Value,
                    item.UpdatedAt ?? item.CreatedAt))
                .ToListAsync(ct);
        var documentRows = caseIds.Length == 0
            ? new List<CaseActivitySignalRow>()
            : await _db.ClientCaseDocuments
                .AsNoTracking()
                .Where(item => caseIds.Contains(item.ClientCaseId))
                .Select(item => new CaseActivitySignalRow(item.ClientCaseId, item.UploadedAt))
                .ToListAsync(ct);
        var courtEventRows = caseIds.Length == 0
            ? new List<CaseActivitySignalRow>()
            : await _db.ClientCaseCourtEvents
                .AsNoTracking()
                .Where(item => caseIds.Contains(item.ClientCaseId))
                .Select(item => new CaseActivitySignalRow(item.ClientCaseId, item.UpdatedAt ?? item.CreatedAt))
                .ToListAsync(ct);

        var tasksByCase = taskRows
            .GroupBy(item => item.ClientCaseId)
            .ToDictionary(group => group.Key, group => group.ToList());
        var commentsByCase = commentRows
            .GroupBy(item => item.ClientCaseId)
            .ToDictionary(group => group.Key, group => group.Select(item => item.LastActivityAt).Max());
        var documentsByCase = documentRows
            .GroupBy(item => item.ClientCaseId)
            .ToDictionary(group => group.Key, group => group.Select(item => item.LastActivityAt).Max());
        var courtEventsByCase = courtEventRows
            .GroupBy(item => item.ClientCaseId)
            .ToDictionary(group => group.Key, group => group.Select(item => item.LastActivityAt).Max());

        var rows = activeCases
            .Select(clientCase =>
            {
                var tasks = tasksByCase.GetValueOrDefault(clientCase.Id) ?? [];
                var activeTaskCount = tasks.Count(item => IsOpenTaskStatus(item.Status));
                var completedTaskCount = tasks.Count - activeTaskCount;
                var lastTaskAt = tasks.Count > 0
                    ? tasks.Max(item => item.LastActivityAt)
                    : (DateTime?)null;
                commentsByCase.TryGetValue(clientCase.Id, out var lastTaskCommentAt);
                documentsByCase.TryGetValue(clientCase.Id, out var lastDocumentAt);
                courtEventsByCase.TryGetValue(clientCase.Id, out var lastCourtEventAt);
                var lastActivityAt = ResolveCaseLastActivityAt(clientCase, lastTaskAt, lastTaskCommentAt, lastDocumentAt, lastCourtEventAt);
                var daysWithoutActivity = Math.Max(0, (now.Date - lastActivityAt.Date).Days);
                var isInactive =
                    activeTaskCount == 0
                    && lastActivityAt <= cutoff
                    && (lastTaskCommentAt == default || lastTaskCommentAt <= cutoff)
                    && (lastDocumentAt == default || lastDocumentAt <= cutoff)
                    && (lastCourtEventAt == default || lastCourtEventAt <= cutoff)
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
                    CreatedAt = NormalizeReportInstant(clientCase.CreatedAt),
                    LastActivityAt = NormalizeReportInstant(lastActivityAt),
                    LastTaskAt = NormalizeReportInstant(lastTaskAt),
                    LastViewedAt = NormalizeReportInstant(clientCase.LastViewedAt),
                    ActivityConfirmedAt = NormalizeReportInstant(clientCase.ActivityConfirmedAt),
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
            GeneratedAt = NormalizeReportInstant(now),
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
            GeneratedAt = NormalizeReportInstant(DateTime.UtcNow),
            AsOf = NormalizeReportDate(asOf),
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
            DueAt = item.AllDay ? item.DueAt : NormalizeReportInstant(item.DueAt),
            AllDay = item.AllDay,
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
        TimeZoneInfo timeZone,
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
                    item.DueAt,
                    item.AllDay))
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
                asOf,
                timeZone))
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
            GeneratedAt = NormalizeReportInstant(DateTime.UtcNow),
            AsOf = NormalizeReportDate(asOf),
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
        DateTime asOf,
        TimeZoneInfo timeZone)
    {
        var startDate = clientCase.StartDate.HasValue
            ? NormalizeReportDate(clientCase.StartDate.Value)
            : ResolveReportLocalDate(clientCase.CreatedAt, timeZone);
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
        var lastActivityAt = ResolveCaseLastActivityAt(clientCase);

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
            CreatedAt = NormalizeReportInstant(clientCase.CreatedAt),
            StartDate = NormalizeReportDate(clientCase.StartDate),
            DueDate = NormalizeReportDate(clientCase.DueDate),
            NextHearingAt = NormalizeReportInstant(clientCase.NextHearingAt),
            DaysInWork = Math.Max(0, (asOf.Date - startDate.Date).Days),
            DaysUntilDue = clientCase.DueDate.HasValue
                ? (clientCase.DueDate.Value.Date - asOf.Date).Days
                : null,
            IsOverdue = clientCase.DueDate.HasValue && clientCase.DueDate.Value.Date < asOf.Date,
            OpenTaskCount = openTasks.Count,
            OverdueTaskCount = overdueTasks.Count,
            OnApprovalTaskCount = onApprovalTasks.Count,
            ActiveDeadlineCount = deadlines.Count,
            OverdueDeadlineCount = overdueDeadlines.Count,
            NextDeadlineAt = nextDeadline is null
                ? null
                : nextDeadline.AllDay ? nextDeadline.DueAt : NormalizeReportInstant(nextDeadline.DueAt),
            NextDeadlineAllDay = nextDeadline?.AllDay ?? true,
            NextDeadlineLabel = nextDeadline is null
                ? string.Empty
                : NormalizeReportText(nextDeadline.Title, ResolveProceduralDeadlineTypeLabel(nextDeadline.DeadlineType)),
            LastActivityAt = NormalizeReportInstant(lastActivityAt),
        };
    }

    private async Task<TaskReportDto> BuildTaskReportAsync(
        DateTime asOf,
        string scope,
        int? assigneeUserId,
        int limit,
        DataVisibilityAccess tasksVisibility,
        CancellationToken ct)
    {
        var query = _db.TaskJournalItems
            .AsNoTracking()
            .Include(task => task.Client)
            .Include(task => task.LegalEntity)
            .Include(task => task.ClientCase)
            .Include(task => task.Participants)
            .ApplyTaskVisibility(tasksVisibility);

        if (assigneeUserId.HasValue)
        {
            query = query.Where(task =>
                task.AssigneeUserId == assigneeUserId.Value ||
                task.Participants.Any(participant =>
                    participant.Role == "assignee" &&
                    participant.UserId == assigneeUserId.Value));
        }

        var rows = (await query.ToListAsync(ct))
            .Select(task => MapTaskReportItem(task, asOf))
            .ToList();

        var openRows = rows.Where(item => !item.IsClosed).ToList();
        var closedRows = rows.Where(item => item.IsClosed).ToList();
        var filteredRows = scope switch
        {
            "closed" => closedRows,
            "all" => rows,
            _ => openRows,
        };

        var orderedRows = filteredRows
            .OrderBy(item => item.IsClosed)
            .ThenByDescending(item => item.IsOverdue)
            .ThenBy(item => item.DueAt)
            .ThenBy(item => item.Title)
            .Take(limit)
            .ToList();

        return new TaskReportDto
        {
            GeneratedAt = NormalizeReportInstant(DateTime.UtcNow),
            AsOf = NormalizeReportDate(asOf),
            Scope = scope,
            AssigneeUserIdFilter = assigneeUserId,
            Limit = limit,
            TotalTaskCount = rows.Count,
            OpenTaskCount = openRows.Count,
            ClosedTaskCount = closedRows.Count,
            OverdueTaskCount = openRows.Count(item => item.IsOverdue),
            DueTodayTaskCount = openRows.Count(item => item.DueAt.Date == asOf.Date),
            WithoutAssigneeCount = rows.Count(item => !item.AssigneeUserId.HasValue && string.IsNullOrWhiteSpace(item.AssigneeName)),
            ByStatus = rows
                .GroupBy(item => new { item.Status, item.StatusLabel })
                .OrderBy(group => TaskStatusSortOrder(group.Key.Status))
                .ThenBy(group => group.Key.StatusLabel)
                .Select(group => new TaskReportStatusItemDto
                {
                    Status = group.Key.Status,
                    StatusLabel = group.Key.StatusLabel,
                    Count = group.Count(),
                    OverdueCount = group.Count(item => item.IsOverdue),
                    Share = rows.Count > 0
                        ? (int)Math.Round((decimal)group.Count() / rows.Count * 100m, MidpointRounding.AwayFromZero)
                        : 0,
                })
                .ToList(),
            ByResponsible = rows
                .GroupBy(item => new
                {
                    item.AssigneeUserId,
                    AssigneeName = NormalizeReportText(item.AssigneeName, "Без исполнителя"),
                })
                .OrderByDescending(group => group.Count(item => !item.IsClosed))
                .ThenBy(group => group.Key.AssigneeName)
                .Select(group => new TaskReportResponsibleItemDto
                {
                    AssigneeUserId = group.Key.AssigneeUserId,
                    AssigneeName = group.Key.AssigneeName,
                    TotalTasks = group.Count(),
                    OpenTasks = group.Count(item => !item.IsClosed),
                    ClosedTasks = group.Count(item => item.IsClosed),
                    OverdueTasks = group.Count(item => item.IsOverdue),
                    NearestDueAt = group
                        .Where(item => !item.IsClosed)
                        .OrderBy(item => item.DueAt)
                        .Select(item => (DateTime?)NormalizeReportInstant(item.DueAt))
                        .FirstOrDefault(),
                })
                .ToList(),
            Items = orderedRows,
        };
    }

    private static TaskReportItemDto MapTaskReportItem(TaskJournalItem task, DateTime asOf)
    {
        var status = NormalizeReportText(task.Status, "new");
        var isClosed = IsClosedTaskStatus(status);
        var isOverdue = !isClosed && task.DueAt.Date < asOf.Date;

        return new TaskReportItemDto
        {
            TaskId = task.Id,
            ClientId = task.ClientId,
            ClientName = task.Client?.Name ?? string.Empty,
            LegalEntityId = task.LegalEntityId,
            LegalEntityName = task.LegalEntity is null
                ? string.Empty
                : NormalizeReportText(task.LegalEntity.ShortName, task.LegalEntity.FullName ?? string.Empty),
            ClientCaseId = task.ClientCaseId,
            ClientCaseTitle = task.ClientCase?.Title ?? string.Empty,
            ObjectName = NormalizeReportText(task.ObjectName, "Без объекта"),
            Title = NormalizeReportText(task.Title, "Без названия"),
            AssigneeUserId = task.AssigneeUserId,
            AssigneeName = NormalizeReportText(ResolveTaskAssigneeName(task), "Без исполнителя"),
            AuthorName = task.AuthorName ?? string.Empty,
            DueAt = NormalizeReportInstant(task.DueAt),
            PlannedDurationMinutes = task.PlannedDurationMinutes,
            Type = task.Type,
            TypeLabel = FormatTaskType(task.Type),
            Priority = task.Priority,
            PriorityLabel = FormatTaskPriority(task.Priority),
            Status = status,
            StatusLabel = FormatTaskStatus(status),
            Result = task.Result ?? string.Empty,
            IsClosed = isClosed,
            IsOverdue = isOverdue,
            DaysOverdue = isOverdue ? Math.Max(0, (asOf.Date - task.DueAt.Date).Days) : null,
            CreatedAt = NormalizeReportInstant(task.CreatedAt),
            UpdatedAt = NormalizeReportInstant(task.UpdatedAt),
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
                    item.DueAt,
                    item.AllDay))
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
            GeneratedAt = NormalizeReportInstant(DateTime.UtcNow),
            AsOf = NormalizeReportDate(asOf),
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
        var nearestDeadline = deadlines
            .OrderBy(item => item.DueAt)
            .FirstOrDefault();
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
            NearestTaskDueAt = tasks.Count == 0 ? null : NormalizeReportInstant(tasks.Min(item => item.DueAt)),
            NearestDeadlineAt = nearestDeadline is null
                ? null
                : nearestDeadline.AllDay ? nearestDeadline.DueAt : NormalizeReportInstant(nearestDeadline.DueAt),
            NearestDeadlineAllDay = nearestDeadline?.AllDay ?? true,
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

    private static bool IsClosedTaskStatus(string? status)
        => string.Equals(status, "done", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeTaskReportScope(string? scope)
    {
        var normalized = scope?.Trim().ToLowerInvariant();
        return normalized is "open" or "closed" or "all" ? normalized : "open";
    }

    private static string ResolveTaskAssigneeName(TaskJournalItem task)
    {
        if (!string.IsNullOrWhiteSpace(task.AssigneeName))
        {
            return task.AssigneeName.Trim();
        }

        return task.Participants
            .Where(participant => string.Equals(participant.Role, "assignee", StringComparison.OrdinalIgnoreCase))
            .Select(participant => participant.DisplayName)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? string.Empty;
    }

    private static int TaskStatusSortOrder(string? status)
        => status switch
        {
            "new" => 0,
            "inProgress" => 1,
            "waiting" => 2,
            "onApproval" => 3,
            "overdueApproval" => 4,
            "approved" => 5,
            "revision" => 6,
            "done" => 7,
            "cancelled" => 8,
            _ => 9,
        };

    private static string FormatTaskStatus(string? status)
        => status switch
        {
            "new" => "Новая",
            "inProgress" => "В работе",
            "waiting" => "Ожидает",
            "onApproval" => "На согласовании",
            "approved" => "Согласовано",
            "revision" => "На доработке",
            "overdueApproval" => "Просрочено на согласовании",
            "done" => "Готово",
            "cancelled" => "Отменена",
            _ => status ?? "—",
        };

    private static string FormatTaskType(string? type)
        => type switch
        {
            "task" => "Задача",
            "budget" => "Бюджет",
            "contact" => "Связаться",
            "document" => "Документ",
            "reminder" => "Не забыть",
            "control" => "Контроль",
            "courtHearing" => "Суд. заседание",
            "meeting" => "Встреча",
            _ => type ?? "—",
        };

    private static string FormatTaskPriority(string? priority)
        => priority switch
        {
            "low" => "Низкий",
            "normal" => "Обычный",
            "high" => "Высокий",
            "urgent" => "Срочно",
            _ => priority ?? "—",
        };

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

    private static DateTime ResolveCaseLastActivityAt(ClientCase clientCase, params DateTime?[] activitySignals)
    {
        var lastActivity = clientCase.CreatedAt;
        foreach (var signal in activitySignals)
        {
            if (signal.HasValue && signal.Value > lastActivity)
            {
                lastActivity = signal.Value;
            }
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
        string? counterpartyName,
        int? clientId,
        int? caseId,
        int? contractId,
        int? incomeTypeId,
        DataVisibilityAccess paymentsVisibility,
        CancellationToken ct)
    {
        var openingRows = await BuildFactQuery(null, from.AddDays(-1), source, accountName, counterpartyName, clientId, caseId, contractId, incomeTypeId, paymentsVisibility)
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

        var periodRows = await BuildFactQuery(from, to, source, accountName, counterpartyName, clientId, caseId, contractId, incomeTypeId, paymentsVisibility)
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

        var dailyRows = await BuildFactQuery(from, to, source, accountName, counterpartyName, clientId, caseId, contractId, incomeTypeId, paymentsVisibility)
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
        string? counterpartyName,
        int? clientId,
        int? caseId,
        int? contractId,
        int? incomeTypeId,
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

        if (!string.IsNullOrWhiteSpace(counterpartyName))
        {
            var counterpartyFilter = counterpartyName.Trim();
            query = query.Where(item =>
                item.CounterpartyName != null &&
                item.CounterpartyName.Contains(counterpartyFilter));
        }

        if (clientId.HasValue && clientId.Value > 0)
        {
            var targetClientId = clientId.Value;
            query = query.Where(item =>
                item.CounterpartyName != null &&
                _db.Clients.Any(client =>
                    client.Id == targetClientId &&
                    ((!string.IsNullOrEmpty(client.Name) && item.CounterpartyName.Contains(client.Name)) ||
                        (!string.IsNullOrEmpty(client.Company) && item.CounterpartyName.Contains(client.Company)))));
        }
        if (caseId.HasValue && caseId.Value > 0)
        {
            var targetCaseId = caseId.Value;
            query = query.Where(item =>
                item.LinkedInvoiceId.HasValue &&
                _db.Payments.Any(payment =>
                    payment.InvoiceId == item.LinkedInvoiceId.Value &&
                    (payment.ClientCaseId == targetCaseId ||
                        payment.InvoiceClientCases.Any(link => link.ClientCaseId == targetCaseId))));
        }
        if (contractId.HasValue && contractId.Value > 0)
        {
            var targetContractId = contractId.Value;
            query = query.Where(item =>
                item.LinkedInvoiceId.HasValue &&
                _db.Payments.Any(payment =>
                    payment.InvoiceId == item.LinkedInvoiceId.Value &&
                    (payment.ContractId == targetContractId ||
                        _db.ContractPayments.Any(link => link.ContractId == targetContractId && link.PaymentId == payment.Id) ||
                        _db.ContractInvoices.Any(link => link.ContractId == targetContractId && link.InvoiceId == payment.Id))));
        }
        if (incomeTypeId.HasValue && incomeTypeId.Value > 0)
        {
            var targetIncomeTypeId = incomeTypeId.Value;
            query = query.Where(item => item.IncomeTypeId == targetIncomeTypeId);
        }

        return ApplyMoneyOperationVisibility(query, paymentsVisibility);
    }

    private async Task<CashFlowForecastReportDto> BuildCashFlowForecastReportAsync(
        DateTime from,
        DateTime to,
        string? accountName,
        string? counterpartyName,
        int? clientId,
        int? caseId,
        int? contractId,
        int? incomeTypeId,
        DataVisibilityAccess paymentsVisibility,
        CancellationToken ct)
    {
        var openingRows = await BuildFactQuery(null, from.AddDays(-1), null, accountName, counterpartyName, clientId, caseId, contractId, incomeTypeId, paymentsVisibility)
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Income = group
                    .Where(item => item.Direction == MoneyOperationDirection.Income)
                    .Sum(item => item.Amount),
                Expense = group
                    .Where(item => item.Direction == MoneyOperationDirection.Expense)
                    .Sum(item => item.Amount),
            })
            .ToListAsync(ct);

        var openingBalance = openingRows.Sum(item => item.Income - item.Expense);

        var forecastQuery = _db.Payments
            .AsNoTracking()
            .ApplyPaymentVisibility(paymentsVisibility)
            .ExcludeInstallmentParentInvoices(_db)
            .Where(item => item.Status != PaymentStatus.Completed && item.Status != PaymentStatus.Cancelled)
            .Where(item => item.Amount > item.PaidAmount);

        if (!string.IsNullOrWhiteSpace(accountName))
        {
            forecastQuery = forecastQuery.Where(item => item.Account == accountName);
        }

        if (!string.IsNullOrWhiteSpace(counterpartyName))
        {
            var counterpartyFilter = counterpartyName.Trim();
            forecastQuery = forecastQuery.Where(item =>
                item.CounterpartyName != null &&
                item.CounterpartyName.Contains(counterpartyFilter));
        }

        if (clientId.HasValue && clientId.Value > 0)
        {
            var targetClientId = clientId.Value;
            forecastQuery = forecastQuery.Where(item =>
                item.ClientId == targetClientId || item.ExpenseClientId == targetClientId);
        }
        if (caseId.HasValue && caseId.Value > 0)
        {
            var targetCaseId = caseId.Value;
            forecastQuery = forecastQuery.Where(item =>
                item.ClientCaseId == targetCaseId ||
                item.InvoiceClientCases.Any(link => link.ClientCaseId == targetCaseId));
        }
        if (contractId.HasValue && contractId.Value > 0)
        {
            var targetContractId = contractId.Value;
            forecastQuery = forecastQuery.Where(item =>
                item.ContractId == targetContractId ||
                _db.ContractPayments.Any(link => link.ContractId == targetContractId && link.PaymentId == item.Id) ||
                _db.ContractInvoices.Any(link => link.ContractId == targetContractId && link.InvoiceId == item.Id));
        }
        if (incomeTypeId.HasValue && incomeTypeId.Value > 0)
        {
            var targetIncomeTypeId = incomeTypeId.Value;
            forecastQuery = forecastQuery.Where(item => item.IncomeTypeId == targetIncomeTypeId);
        }

        var dailyRows = await forecastQuery
            .Where(item => item.Date.Date >= from.Date && item.Date.Date <= to.Date)
            .GroupBy(item => item.Date.Date)
            .Select(group => new CashFlowForecastDayQueryRow
            {
                Date = group.Key,
                ExpectedIncome = group
                    .Where(item => item.Type == PaymentType.Income)
                    .Sum(item => item.Amount - item.PaidAmount),
                ExpectedExpense = group
                    .Where(item => item.Type == PaymentType.Expense)
                    .Sum(item => item.Amount - item.PaidAmount),
                IncomeCount = group.Count(item => item.Type == PaymentType.Income),
                ExpenseCount = group.Count(item => item.Type == PaymentType.Expense),
            })
            .OrderBy(item => item.Date)
            .ToListAsync(ct);

        var runningBalance = openingBalance;
        var days = dailyRows
            .Select(row =>
            {
                runningBalance += row.ExpectedIncome - row.ExpectedExpense;

                return new CashFlowForecastDayDto
                {
                    Date = row.Date,
                    ExpectedIncome = row.ExpectedIncome,
                    ExpectedExpense = row.ExpectedExpense,
                    NetProjectedFlow = row.ExpectedIncome - row.ExpectedExpense,
                    ProjectedClosingBalance = runningBalance,
                    IncomeCount = row.IncomeCount,
                    ExpenseCount = row.ExpenseCount,
                };
            })
            .ToList();

        var expectedIncome = dailyRows.Sum(item => item.ExpectedIncome);
        var expectedExpense = dailyRows.Sum(item => item.ExpectedExpense);
        var netProjectedFlow = expectedIncome - expectedExpense;

        return new CashFlowForecastReportDto
        {
            From = from,
            To = to,
            AccountName = accountName,
            OpeningBalance = openingBalance,
            ExpectedIncome = expectedIncome,
            ExpectedExpense = expectedExpense,
            NetProjectedFlow = netProjectedFlow,
            ProjectedClosingBalance = openingBalance + netProjectedFlow,
            IncomeCount = dailyRows.Sum(item => item.IncomeCount),
            ExpenseCount = dailyRows.Sum(item => item.ExpenseCount),
            Days = days,
        };
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
        string? accountName,
        string? counterpartyName,
        int? clientId,
        int? caseId,
        int? contractId,
        int? incomeTypeId,
        DataVisibilityAccess paymentsVisibility,
        CancellationToken ct)
    {
        var query = BuildFactQuery(from, to, source, accountName, counterpartyName, clientId, caseId, contractId, incomeTypeId, paymentsVisibility)
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
        string? accountName,
        string? counterpartyName,
        int? clientId,
        int? caseId,
        int? contractId,
        int? incomeTypeId,
        DataVisibilityAccess paymentsVisibility,
        CancellationToken ct)
    {
        var rows = await BuildFactQuery(from, to, source, accountName, counterpartyName, clientId, caseId, contractId, incomeTypeId, paymentsVisibility)
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
        string? accountName,
        string? counterpartyName,
        int? clientId,
        int? caseId,
        int? contractId,
        int? incomeTypeId,
        DataVisibilityAccess paymentsVisibility,
        CancellationToken ct)
    {
        var rows = await BuildFactQuery(from, to, source, accountName, counterpartyName, clientId, caseId, contractId, incomeTypeId, paymentsVisibility)
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
        string? accountName,
        string? counterpartyName,
        int? clientId,
        int? caseId,
        int? contractId,
        int? incomeTypeId,
        DataVisibilityAccess paymentsVisibility,
        CancellationToken ct)
    {
        var rows = await BuildFactQuery(from, to, source, accountName, counterpartyName, clientId, caseId, contractId, incomeTypeId, paymentsVisibility)
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
        string? accountName,
        string? counterpartyName,
        int? clientId,
        int? caseId,
        int? contractId,
        int? incomeTypeId,
        DataVisibilityAccess paymentsVisibility,
        CancellationToken ct)
    {
        var rows = await BuildFactQuery(from, to, source, accountName, counterpartyName, clientId, caseId, contractId, incomeTypeId, paymentsVisibility)
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
        string? accountName,
        string? counterpartyName,
        int? clientId,
        int? caseId,
        int? contractId,
        int? incomeTypeId,
        DataVisibilityAccess paymentsVisibility,
        CancellationToken ct)
    {
        var rows = await BuildFactQuery(from, to, source, accountName, counterpartyName, clientId, caseId, contractId, incomeTypeId, paymentsVisibility)
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
        string? accountName,
        string? counterpartyName,
        int? clientId,
        int? caseId,
        int? contractId,
        int? incomeTypeId,
        DataVisibilityAccess paymentsVisibility,
        CancellationToken ct)
    {
        var rows = await BuildPlanFactQuery(from, to, source, accountName, counterpartyName, clientId, caseId, contractId, incomeTypeId, paymentsVisibility)
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
        string? accountName,
        string? counterpartyName,
        int? clientId,
        int? caseId,
        int? contractId,
        int? incomeTypeId,
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

        if (!string.IsNullOrWhiteSpace(accountName))
        {
            query = query.Where(item => item.AccountName == accountName);
        }

        if (!string.IsNullOrWhiteSpace(counterpartyName))
        {
            var counterpartyFilter = counterpartyName.Trim();
            query = query.Where(item =>
                item.CounterpartyName != null &&
                item.CounterpartyName.Contains(counterpartyFilter));
        }

        if (clientId.HasValue && clientId.Value > 0)
        {
            var targetClientId = clientId.Value;
            query = query.Where(item =>
                item.CounterpartyName != null &&
                _db.Clients.Any(client =>
                    client.Id == targetClientId &&
                    ((!string.IsNullOrEmpty(client.Name) && item.CounterpartyName.Contains(client.Name)) ||
                        (!string.IsNullOrEmpty(client.Company) && item.CounterpartyName.Contains(client.Company)))));
        }
        if (caseId.HasValue && caseId.Value > 0)
        {
            var targetCaseId = caseId.Value;
            query = query.Where(item =>
                item.LinkedInvoiceId.HasValue &&
                _db.Payments.Any(payment =>
                    payment.InvoiceId == item.LinkedInvoiceId.Value &&
                    (payment.ClientCaseId == targetCaseId ||
                        payment.InvoiceClientCases.Any(link => link.ClientCaseId == targetCaseId))));
        }
        if (contractId.HasValue && contractId.Value > 0)
        {
            var targetContractId = contractId.Value;
            query = query.Where(item =>
                item.LinkedInvoiceId.HasValue &&
                _db.Payments.Any(payment =>
                    payment.InvoiceId == item.LinkedInvoiceId.Value &&
                    (payment.ContractId == targetContractId ||
                        _db.ContractPayments.Any(link => link.ContractId == targetContractId && link.PaymentId == payment.Id) ||
                        _db.ContractInvoices.Any(link => link.ContractId == targetContractId && link.InvoiceId == payment.Id))));
        }
        if (incomeTypeId.HasValue && incomeTypeId.Value > 0)
        {
            var targetIncomeTypeId = incomeTypeId.Value;
            query = query.Where(item => item.IncomeTypeId == targetIncomeTypeId);
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
        string? accountName,
        string? counterpartyName,
        int? clientId,
        int? caseId,
        int? contractId,
        int? incomeTypeId,
        DataVisibilityAccess paymentsVisibility,
        CancellationToken ct)
    {
        var rows = await BuildFactQuery(from, to, source, accountName, counterpartyName, clientId, caseId, contractId, incomeTypeId, paymentsVisibility)
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

        List<AdvanceReportUnallocatedBalanceRow> unallocatedBalances = source.HasValue || !string.IsNullOrWhiteSpace(accountName)
            ? []
            : await BuildUnallocatedAdvanceBalanceRowsAsync(
                to,
                counterpartyName,
                clientId,
                caseId,
                contractId,
                incomeTypeId,
                paymentsVisibility,
                ct);

        var moneyByCounterparty = enriched
            .GroupBy(item => item.CounterpartyName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.First().CounterpartyName,
                group => new
                {
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
                },
                StringComparer.OrdinalIgnoreCase);

        var unallocatedByCounterparty = unallocatedBalances
            .GroupBy(item => item.CounterpartyName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.First().CounterpartyName,
                group => new
                {
                    Total = group.Sum(item => item.AvailableAmount),
                    Count = group.Count(),
                },
                StringComparer.OrdinalIgnoreCase);

        var counterpartyNames = moneyByCounterparty.Keys
            .Concat(unallocatedByCounterparty.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var counterparties = counterpartyNames
            .Select(name =>
            {
                moneyByCounterparty.TryGetValue(name, out var money);
                unallocatedByCounterparty.TryGetValue(name, out var unallocated);

                return new AdvanceReportItemDto
                {
                    CounterpartyName = name,
                    IncomeTotal = money?.IncomeTotal ?? 0m,
                    ExpenseTotal = money?.ExpenseTotal ?? 0m,
                    RecognizedTotal = money?.RecognizedTotal ?? 0m,
                    UnrecognizedTotal = money?.UnrecognizedTotal ?? 0m,
                    UnallocatedBalanceTotal = unallocated?.Total ?? 0m,
                    IncomeCount = money?.IncomeCount ?? 0,
                    ExpenseCount = money?.ExpenseCount ?? 0,
                    RecognizedCount = money?.RecognizedCount ?? 0,
                    UnrecognizedCount = money?.UnrecognizedCount ?? 0,
                    UnallocatedBalanceCount = unallocated?.Count ?? 0,
                };
            })
            .OrderByDescending(item => item.IncomeTotal + item.ExpenseTotal + item.UnallocatedBalanceTotal)
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
            UnallocatedBalanceTotal = unallocatedBalances.Sum(item => item.AvailableAmount),
            RecognizedCount = enriched.Count(item => item.IsRecognized),
            UnrecognizedCount = enriched.Count(item => !item.IsRecognized),
            UnallocatedBalanceCount = unallocatedBalances.Count,
            Counterparties = counterparties,
        };
    }

    private async Task<List<AdvanceReportUnallocatedBalanceRow>> BuildUnallocatedAdvanceBalanceRowsAsync(
        DateTime to,
        string? counterpartyName,
        int? clientId,
        int? caseId,
        int? contractId,
        int? incomeTypeId,
        DataVisibilityAccess paymentsVisibility,
        CancellationToken ct)
    {
        var toExclusive = to.Date.AddDays(1);
        var query = _db.ClientBalanceTransactions
            .AsNoTracking()
            .Where(item =>
                item.CreatedAt < toExclusive &&
                item.PaymentId.HasValue &&
                item.Direction == ClientBalanceDirection.Credit &&
                item.Type == ClientBalanceTransactionType.UnallocatedAdvance)
            .Where(item => _db.Payments
                .AsNoTracking()
                .ApplyPaymentVisibility(paymentsVisibility)
                .Any(payment => payment.Id == item.PaymentId));

        if (!string.IsNullOrWhiteSpace(counterpartyName))
        {
            var counterpartyFilter = counterpartyName.Trim();
            query = query.Where(item =>
                (item.Client != null &&
                    ((!string.IsNullOrEmpty(item.Client.Name) && item.Client.Name.Contains(counterpartyFilter)) ||
                        (!string.IsNullOrEmpty(item.Client.Company) && item.Client.Company.Contains(counterpartyFilter)))) ||
                (item.LegalEntity != null &&
                    ((!string.IsNullOrEmpty(item.LegalEntity.ShortName) && item.LegalEntity.ShortName.Contains(counterpartyFilter)) ||
                        (!string.IsNullOrEmpty(item.LegalEntity.FullName) && item.LegalEntity.FullName.Contains(counterpartyFilter)))));
        }

        if (clientId.HasValue && clientId.Value > 0)
        {
            var targetClientId = clientId.Value;
            query = query.Where(item => item.ClientId == targetClientId);
        }

        if (caseId.HasValue && caseId.Value > 0)
        {
            var targetCaseId = caseId.Value;
            query = query.Where(item =>
                item.Payment != null &&
                (item.Payment.ClientCaseId == targetCaseId ||
                    item.Payment.InvoiceClientCases.Any(link => link.ClientCaseId == targetCaseId)));
        }

        if (contractId.HasValue && contractId.Value > 0)
        {
            var targetContractId = contractId.Value;
            query = query.Where(item =>
                item.Payment != null &&
                (item.Payment.ContractId == targetContractId ||
                    _db.ContractPayments.Any(link => link.ContractId == targetContractId && link.PaymentId == item.PaymentId)));
        }

        if (incomeTypeId.HasValue && incomeTypeId.Value > 0)
        {
            var targetIncomeTypeId = incomeTypeId.Value;
            query = query.Where(item => item.Payment != null && item.Payment.IncomeTypeId == targetIncomeTypeId);
        }

        var credits = await query
            .Select(item => new
            {
                PaymentId = item.PaymentId!.Value,
                item.ClientId,
                item.LegalEntityId,
                item.Amount,
                ClientName = item.Client != null
                    ? (!string.IsNullOrEmpty(item.Client.Name) ? item.Client.Name : item.Client.Company)
                    : null,
                LegalEntityName = item.LegalEntity != null
                    ? (!string.IsNullOrEmpty(item.LegalEntity.ShortName) ? item.LegalEntity.ShortName : item.LegalEntity.FullName)
                    : null,
            })
            .ToListAsync(ct);

        if (credits.Count == 0)
        {
            return [];
        }

        var sourcePaymentIds = credits.Select(item => item.PaymentId).ToHashSet();
        var balanceDebits = await _db.ClientBalanceTransactions
            .AsNoTracking()
            .Where(item =>
                item.CreatedAt < toExclusive &&
                item.Direction == ClientBalanceDirection.Debit &&
                item.ExternalKey != null &&
                (item.ExternalKey.StartsWith("payment-unallocated-advance-apply:") ||
                    item.ExternalKey.StartsWith("payment-unallocated-advance-refund:")))
            .Select(item => new
            {
                item.ExternalKey,
                item.Amount,
            })
            .ToListAsync(ct);

        var debitsBySourcePayment = balanceDebits
            .Select(item => new
            {
                SourcePaymentId = TryParseUnallocatedAdvanceSourcePaymentId(item.ExternalKey),
                item.Amount,
            })
            .Where(item => item.SourcePaymentId.HasValue && sourcePaymentIds.Contains(item.SourcePaymentId.Value))
            .GroupBy(item => item.SourcePaymentId!.Value)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Amount));

        return credits
            .Select(item =>
            {
                debitsBySourcePayment.TryGetValue(item.PaymentId, out var appliedOrRefunded);
                var available = Math.Round(Math.Max(item.Amount - appliedOrRefunded, 0m), 2, MidpointRounding.AwayFromZero);

                return new AdvanceReportUnallocatedBalanceRow(
                    item.PaymentId,
                    ResolveCounterpartyName(item.ClientName ?? item.LegalEntityName),
                    available);
            })
            .Where(item => item.AvailableAmount > 0.009m)
            .ToList();
    }

    private static int? TryParseUnallocatedAdvanceSourcePaymentId(string? externalKey)
    {
        if (string.IsNullOrWhiteSpace(externalKey))
        {
            return null;
        }

        const string applyPrefix = "payment-unallocated-advance-apply:";
        const string refundPrefix = "payment-unallocated-advance-refund:";
        string payload;

        if (externalKey.StartsWith(applyPrefix, StringComparison.Ordinal))
        {
            payload = externalKey[applyPrefix.Length..];
        }
        else if (externalKey.StartsWith(refundPrefix, StringComparison.Ordinal))
        {
            payload = externalKey[refundPrefix.Length..];
        }
        else
        {
            return null;
        }

        var separatorIndex = payload.IndexOf(':', StringComparison.Ordinal);
        var sourcePaymentIdText = separatorIndex >= 0 ? payload[..separatorIndex] : payload;
        return int.TryParse(sourcePaymentIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sourcePaymentId)
            ? sourcePaymentId
            : null;
    }

    private sealed record AdvanceReportUnallocatedBalanceRow(
        int PaymentId,
        string CounterpartyName,
        decimal AvailableAmount);

    private async Task<PaymentPriorityReportDto> BuildPaymentPriorityReportAsync(
        DateTime from,
        DateTime to,
        MoneyOperationSource? source,
        string? accountName,
        string? counterpartyName,
        int? clientId,
        int? caseId,
        int? contractId,
        int? incomeTypeId,
        DataVisibilityAccess paymentsVisibility,
        CancellationToken ct)
    {
        var rows = await BuildFactQuery(from, to, source, accountName, counterpartyName, clientId, caseId, contractId, incomeTypeId, paymentsVisibility)
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
        string? accountName,
        string? counterpartyName,
        int? clientId,
        int? caseId,
        int? contractId,
        int? incomeTypeId,
        DataVisibilityAccess paymentsVisibility,
        CancellationToken ct)
    {
        var rows = await BuildFactQuery(from, to, source, accountName, counterpartyName, clientId, caseId, contractId, incomeTypeId, paymentsVisibility)
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
        string? accountName,
        string? counterpartyName,
        int? clientId,
        int? caseId,
        int? contractId,
        int? incomeTypeId,
        DataVisibilityAccess paymentsVisibility,
        CancellationToken ct)
    {
        var rows = await BuildFactQuery(from, to, source, accountName, counterpartyName, clientId, caseId, contractId, incomeTypeId, paymentsVisibility)
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
        var periodStart = NormalizeReportDate(from);
        var periodEnd = NormalizeReportDate(to);
        var periodEndExclusive = periodEnd.AddDays(1);

        var rows = await _db.Acts
            .AsNoTracking()
            .ApplyActVisibility(actsVisibility)
            .Where(item => item.Date >= periodStart && item.Date < periodEndExclusive)
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
            From = periodStart,
            To = periodEnd,
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
        var periodStart = NormalizeReportDate(from);
        var periodEnd = NormalizeReportDate(to);
        var periodEndExclusive = periodEnd.AddDays(1);

        var rows = await _db.Contracts
            .AsNoTracking()
            .ApplyContractVisibility(contractsVisibility)
            .Where(item => item.Date >= periodStart && item.Date < periodEndExclusive)
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
                        item.ValidUntil.Value >= periodStart &&
                        item.ValidUntil.Value < periodEndExclusive),
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
            From = periodStart,
            To = periodEnd,
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
        string? accountName,
        string? counterpartyName,
        int? clientId,
        int? caseId,
        int? contractId,
        int? incomeTypeId,
        DataVisibilityAccess paymentsVisibility,
        CancellationToken ct)
    {
        var rows = await BuildFactQuery(from, to, source, accountName, counterpartyName, clientId, caseId, contractId, incomeTypeId, paymentsVisibility)
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
        string? accountName,
        string? counterpartyName,
        int? clientId,
        int? caseId,
        int? contractId,
        int? incomeTypeId,
        DataVisibilityAccess paymentsVisibility,
        CancellationToken ct)
    {
        var rows = await BuildFactQuery(from, to, source, accountName, counterpartyName, clientId, caseId, contractId, incomeTypeId, paymentsVisibility)
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
        string? accountName,
        string? counterpartyName,
        int? clientId,
        int? caseId,
        int? contractId,
        int? incomeTypeId,
        DataVisibilityAccess paymentsVisibility,
        CancellationToken ct)
    {
        var rows = await BuildFactQuery(from, to, source, accountName, counterpartyName, clientId, caseId, contractId, incomeTypeId, paymentsVisibility)
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
        string? accountName,
        string? counterpartyName,
        int? clientId,
        int? caseId,
        int? contractId,
        int? incomeTypeId,
        DataVisibilityAccess paymentsVisibility,
        CancellationToken ct)
    {
        var rows = await BuildFactQuery(from, to, source, accountName, counterpartyName, clientId, caseId, contractId, incomeTypeId, paymentsVisibility)
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
        string? accountName,
        string? counterpartyName,
        int? clientId,
        int? caseId,
        int? contractId,
        int? incomeTypeId,
        DataVisibilityAccess paymentsVisibility,
        CancellationToken ct)
    {
        var rows = await BuildFactQuery(from, to, source, accountName, counterpartyName, clientId, caseId, contractId, incomeTypeId, paymentsVisibility)
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

    private async Task<CashBookResponseDto> BuildCashBookReportAsync(
        DateTime from,
        DateTime to,
        CancellationToken ct)
    {
        var postedOrders = _db.CashOrders
            .AsNoTracking()
            .Where(order => order.Status == CashOrderStatus.Posted);

        var openingBalance = await postedOrders
            .Where(order => order.Date < from)
            .SumAsync(order => order.Type == CashOrderType.Incoming ? order.Amount : -order.Amount, ct);

        var periodGroups = await postedOrders
            .Where(order => order.Date >= from && order.Date <= to)
            .GroupBy(order => order.Date.Date)
            .Select(group => new
            {
                Date = group.Key,
                Income = group.Where(order => order.Type == CashOrderType.Incoming).Sum(order => order.Amount),
                Expense = group.Where(order => order.Type == CashOrderType.Outgoing).Sum(order => order.Amount),
                IncomingCount = group.Count(order => order.Type == CashOrderType.Incoming),
                OutgoingCount = group.Count(order => order.Type == CashOrderType.Outgoing),
            })
            .OrderBy(group => group.Date)
            .ToListAsync(ct);
        var periodGroupsByDate = periodGroups.ToDictionary(group => group.Date);

        var runningBalance = openingBalance;
        var days = new List<CashBookDayDto>((to - from).Days + 1);
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            periodGroupsByDate.TryGetValue(date, out var group);
            var dayOpening = runningBalance;
            var dayIncome = group?.Income ?? 0m;
            var dayExpense = group?.Expense ?? 0m;
            runningBalance += dayIncome - dayExpense;

            days.Add(new CashBookDayDto
            {
                Date = date,
                OpeningBalance = dayOpening,
                Income = dayIncome,
                Expense = dayExpense,
                ClosingBalance = runningBalance,
                IncomingCount = group?.IncomingCount ?? 0,
                OutgoingCount = group?.OutgoingCount ?? 0,
            });
        }

        var income = periodGroups.Sum(group => group.Income);
        var expense = periodGroups.Sum(group => group.Expense);

        return new CashBookResponseDto
        {
            From = from,
            To = to,
            OpeningBalance = openingBalance,
            Income = income,
            Expense = expense,
            ClosingBalance = openingBalance + income - expense,
            IncomingCount = periodGroups.Sum(group => group.IncomingCount),
            OutgoingCount = periodGroups.Sum(group => group.OutgoingCount),
            Days = days,
        };
    }

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
        TimeZoneInfo timeZone,
        CancellationToken ct)
    {
        var localStart = from.Date;
        var localEnd = to.Date;
        var startUtc = CalendarTimeZoneHelper.FromUserLocalToUtc(localStart, timeZone);
        var endUtc = CalendarTimeZoneHelper.FromUserLocalToUtc(localEnd.AddDays(1), timeZone);
        var rows = await _db.BankStatementImportBatches
            .AsNoTracking()
            .Where(item => item.CreatedAt >= startUtc && item.CreatedAt < endUtc)
            .Select(item => new
            {
                item.Status,
                item.RowsRead,
                item.CreatedRows,
                item.SkippedRows,
                item.ErrorRows,
                item.RollbackRows,
                item.CreatedAt,
            })
            .ToListAsync(ct);

        rows = rows
            .Where(item =>
            {
                var localDate = CalendarTimeZoneHelper.ToUserLocal(item.CreatedAt, timeZone).Date;
                return localDate >= localStart && localDate <= localEnd;
            })
            .ToList();

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
        TimeZoneInfo timeZone,
        CancellationToken ct)
    {
        var localStart = from.Date;
        var localEnd = to.Date;
        var startUtc = CalendarTimeZoneHelper.FromUserLocalToUtc(localStart, timeZone);
        var endUtc = CalendarTimeZoneHelper.FromUserLocalToUtc(localEnd.AddDays(1), timeZone);
        var rows = await _db.SpreadsheetImportBatches
            .AsNoTracking()
            .Where(item => item.CreatedAt >= startUtc && item.CreatedAt < endUtc)
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
                item.CreatedAt,
            })
            .ToListAsync(ct);

        rows = rows
            .Where(item =>
            {
                var localDate = CalendarTimeZoneHelper.ToUserLocal(item.CreatedAt, timeZone).Date;
                return localDate >= localStart && localDate <= localEnd;
            })
            .ToList();

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

    private sealed record ReportExportDocument(
        string Slug,
        string Title,
        string Subtitle,
        IReadOnlyList<ReportExportMetric> Metrics,
        IReadOnlyList<ReportExportTable> Tables);

    private sealed record ReportExportMetric(
        string Label,
        string Value);

    private sealed record ReportExportTable(
        string Title,
        IReadOnlyList<string> Headers,
        IReadOnlyList<ReportExportRow> Rows);

    private sealed record ReportExportRow(
        IReadOnlyList<string> Cells);

    private sealed record CaseActivityTaskRow(
        int ClientCaseId,
        string Status,
        DateTime LastActivityAt);

    private sealed record CaseActivitySignalRow(
        int ClientCaseId,
        DateTime LastActivityAt);

    private sealed record CaseActivityOwner(
        int? OwnerId,
        string OwnerType,
        string OwnerName);

    private sealed record CurrentCaseDeadlineRow(
        int ClientCaseId,
        string DeadlineType,
        string Title,
        DateTime DueAt,
        bool AllDay);

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

    private sealed class CashFlowForecastDayQueryRow
    {
        public DateTime Date { get; init; }
        public decimal ExpectedIncome { get; init; }
        public decimal ExpectedExpense { get; init; }
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
