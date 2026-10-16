using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Helpers;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Invoices;
using PayPlanner.Api.Services.Banking;
using static PayPlanner.Api.Services.Banking.BankStatementImportRules;
using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/money-operations")]
[Authorize]
public partial class MoneyOperationsController : ControllerBase
{
    private const long MaxStatementFileSizeBytes = 10L * 1024L * 1024L;
    private const int MaxAutoImportStatementFiles = 20;
    private const string BankStatementAutoImportSettingKey = "money.bankStatement.autoImport";
    private const string BankStatementCategoryRulesSettingKey = "money.bankStatement.categoryRules";
    private readonly PaymentContext _db;
    private readonly ILogger<MoneyOperationsController> _logger;
    private readonly IInvoiceDocumentPdfService? _pdfService;
    private static readonly JsonSerializerOptions BankStatementAutoImportJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions BankStatementCategoryRulesJsonOptions = CreateBankStatementCategoryRulesJsonOptions();

    public MoneyOperationsController(
        PaymentContext db,
        ILogger<MoneyOperationsController> logger,
        IInvoiceDocumentPdfService? pdfService = null)
    {
        _db = db;
        _logger = logger;
        _pdfService = pdfService;
    }

    private async Task<DataVisibilityAccess?> ResolveMoneyVisibilityAsync(CancellationToken ct)
    {
        var access = await ResolveMoneyAccessAsync(ct);
        moneyInvoiceReadAccess = access is null ? null
            : await _db.ResolveSectionAccessAsync(User, RolePermissionCatalog.Accounts, ct, requireApprovedUser: true);
        moneyContractReadAccess = access is null ? null
            : await _db.ResolveSectionAccessAsync(User, RolePermissionCatalog.Contracts, ct, requireApprovedUser: true);
        moneyActReadAccess = access is null ? null
            : await _db.ResolveSectionAccessAsync(User, RolePermissionCatalog.Acts, ct, requireApprovedUser: true);
        return access?.Visibility;
    }

    private async Task<ForbidResult?> RequirePaymentsPermissionAsync(
        Func<RolePermissionValues, bool> isAllowed,
        CancellationToken ct)
        => await ResolveMoneyAccessAsync(ct) is { } access && access.Permission.CanView && isAllowed(access.Permission) ? null : Forbid();

    private async Task<bool> CanExportMoneyOperationsAsync(CancellationToken ct)
    {
        var userId = GetCurrentUserId();
        if (!userId.HasValue)
        {
            return User?.Identity?.IsAuthenticated != true;
        }

        if (await _db.IsUserAdminAsync(userId.Value, ct))
        {
            return true;
        }

        var permission = await _db.GetUserSectionPermissionAsync(userId.Value, RolePermissionCatalog.Payments, ct);
        return permission?.CanExport == true;
    }

    private static IQueryable<MoneyOperationQueryRow> ApplyMoneyOperationVisibility(
        IQueryable<MoneyOperationQueryRow> query, DataVisibilityAccess? visibility)
        => MoneyJournalQuery.ApplyVisibility(query, visibility);

    private async Task<bool> CanAccessCustomMoneyOperationAsync(
        int id,
        DataVisibilityAccess visibility,
        CancellationToken ct)
        => await ApplyMoneyOperationVisibility(
                BuildMoneyOperationsQuery(null, null, null, null, null, null, null, null, null),
                visibility)
            .AnyAsync(item => item.Id == id && !item.IsReadonly, ct);

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] MoneyOperationSource? source,
        [FromQuery] MoneyOperationDirection? direction,
        [FromQuery] MoneyOperationAccountingType? accountingType,
        [FromQuery] int? moneyAccountId = null,
        [FromQuery] string? accountName = null,
        [FromQuery] string? categoryName = null,
        [FromQuery] int? linkedInvoiceId = null,
        [FromQuery] string? search = null,
        CancellationToken ct = default)
    {
        var visibility = await ResolveMoneyVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var list = await SqliteRetryHelper.ExecuteAsync(
            async cancellationToken =>
            {
                var query = ApplyMoneyOperationVisibility(
                    BuildMoneyOperationsQuery(from, to, source, direction, accountingType, moneyAccountId, accountName, categoryName, search),
                    visibility);
                var rows = await query
                    .Where(item => !linkedInvoiceId.HasValue || item.LinkedInvoiceId == linkedInvoiceId.Value)
                    .OrderByDescending(item => item.Date)
                    .ThenByDescending(item => item.Id)
                    .ToListAsync(cancellationToken);

                return rows.Select(MapMoneyOperationRow).ToList();
            },
            _logger,
            ct);

        return Ok(list);
    }

    [HttpGet("paged")]
    public async Task<ActionResult<MoneyOperationsPageResponse>> GetPaged(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] MoneyOperationSource? source,
        [FromQuery] MoneyOperationDirection? direction,
        [FromQuery] MoneyOperationAccountingType? accountingType,
        [FromQuery] int? moneyAccountId = null,
        [FromQuery] string? accountName = null,
        [FromQuery] string? categoryName = null,
        [FromQuery] int? linkedInvoiceId = null,
        [FromQuery] string? search = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 5, 200);
        var visibility = await ResolveMoneyVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var response = await SqliteRetryHelper.ExecuteAsync(async cancellationToken =>
        {
            var query = ApplyMoneyOperationVisibility(
                    BuildMoneyOperationsQuery(from, to, source, direction, accountingType, moneyAccountId, accountName, categoryName, search),
                    visibility)
                .Where(item => !linkedInvoiceId.HasValue || item.LinkedInvoiceId == linkedInvoiceId.Value);
            var total = await query.CountAsync(cancellationToken);
            var itemRows = await query
                .OrderByDescending(item => item.Date)
                .ThenByDescending(item => item.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(cancellationToken);
            var items = itemRows.Select(MapMoneyOperationRow).ToList();

            var factSummary = await query
                .Where(item => item.AccountingType == MoneyOperationAccountingType.Fact.ToString())
                .GroupBy(_ => 1)
                .Select(group => new
                {
                    Income = group
                        .Where(item => item.Direction == MoneyOperationDirection.Income.ToString())
                        .Sum(item => item.Amount),
                    Expense = group
                        .Where(item => item.Direction == MoneyOperationDirection.Expense.ToString())
                        .Sum(item => item.Amount),
                    IncomeCount = group.Count(item => item.Direction == MoneyOperationDirection.Income.ToString()),
                    ExpenseCount = group.Count(item => item.Direction == MoneyOperationDirection.Expense.ToString()),
                })
                .FirstOrDefaultAsync(cancellationToken);

            var openingQuery = from.HasValue
                ? ApplyMoneyOperationVisibility(
                    BuildMoneyOperationsQuery(null, from.Value.Date.AddDays(-1), source, direction, MoneyOperationAccountingType.Fact, moneyAccountId, accountName, categoryName, search),
                    visibility)
                : null;

            var openingSummary = openingQuery is null
                ? null
                : await openingQuery
                    .GroupBy(_ => 1)
                    .Select(group => new
                    {
                        Income = group
                            .Where(item => item.Direction == MoneyOperationDirection.Income.ToString())
                            .Sum(item => item.Amount),
                        Expense = group
                            .Where(item => item.Direction == MoneyOperationDirection.Expense.ToString())
                            .Sum(item => item.Amount),
                    })
                    .FirstOrDefaultAsync(cancellationToken);

            var openingRows = new List<MoneyOperationSourceSummaryDto>();
            if (openingQuery is not null)
            {
                var openingSummaryRows = await openingQuery
                    .GroupBy(item => item.Source)
                    .Select(group => new MoneyOperationSourceSummaryQueryRow
                    {
                        Source = group.Key,
                        StartBalance = 0m,
                        Income = group
                            .Where(item => item.Direction == MoneyOperationDirection.Income.ToString())
                            .Sum(item => item.Amount),
                        Expense = group
                            .Where(item => item.Direction == MoneyOperationDirection.Expense.ToString())
                            .Sum(item => item.Amount),
                        EndBalance =
                            group.Where(item => item.Direction == MoneyOperationDirection.Income.ToString()).Sum(item => item.Amount) -
                            group.Where(item => item.Direction == MoneyOperationDirection.Expense.ToString()).Sum(item => item.Amount),
                    })
                    .ToListAsync(cancellationToken);
                openingRows = openingSummaryRows.Select(MapSourceSummaryRow).ToList();
            }

            var sourceRows = await query
                .Where(item => item.AccountingType == MoneyOperationAccountingType.Fact.ToString())
                .GroupBy(item => item.Source)
                .Select(group => new MoneyOperationSourceSummaryQueryRow
                {
                    Source = group.Key,
                    StartBalance = 0m,
                    Income = group
                        .Where(item => item.Direction == MoneyOperationDirection.Income.ToString())
                        .Sum(item => item.Amount),
                    Expense = group
                        .Where(item => item.Direction == MoneyOperationDirection.Expense.ToString())
                        .Sum(item => item.Amount),
                    EndBalance =
                        group.Where(item => item.Direction == MoneyOperationDirection.Income.ToString()).Sum(item => item.Amount) -
                        group.Where(item => item.Direction == MoneyOperationDirection.Expense.ToString()).Sum(item => item.Amount),
                })
                .ToListAsync(cancellationToken);
            var sourceSummaries = sourceRows.Select(MapSourceSummaryRow).ToList();

            var orderedSources = new[]
            {
                MoneyOperationSource.Cash,
                MoneyOperationSource.Bank,
                MoneyOperationSource.Card,
                MoneyOperationSource.EMoney,
            };

            var orderedSourceRows = orderedSources
                .Select(sourceKey =>
                {
                    var periodRow = sourceSummaries.FirstOrDefault(row => row.Source == sourceKey);
                    var openingRow = openingRows.FirstOrDefault(row => row.Source == sourceKey);
                    var startBalance = openingRow?.EndBalance ?? 0m;
                    var incomeAmount = periodRow?.Income ?? 0m;
                    var expenseAmount = periodRow?.Expense ?? 0m;

                    return new MoneyOperationSourceSummaryDto
                    {
                        Source = sourceKey,
                        StartBalance = startBalance,
                        Income = incomeAmount,
                        Expense = expenseAmount,
                        EndBalance = startBalance + incomeAmount - expenseAmount,
                    };
                })
                .ToList();

            var income = factSummary?.Income ?? 0m;
            var expense = factSummary?.Expense ?? 0m;
            var startBalance = (openingSummary?.Income ?? 0m) - (openingSummary?.Expense ?? 0m);

            return new MoneyOperationsPageResponse
            {
                Items = items,
                Total = total,
                Page = page,
                PageSize = pageSize,
                Summary = new MoneyOperationsSummaryDto
                {
                    StartBalance = startBalance,
                    Income = income,
                    Expense = expense,
                    EndBalance = startBalance + income - expense,
                    IncomeCount = factSummary?.IncomeCount ?? 0,
                    ExpenseCount = factSummary?.ExpenseCount ?? 0,
                    BySource = orderedSourceRows,
                },
            };
        }, _logger, ct);

        return Ok(response);
    }

    [HttpGet("export.csv")]
    public async Task<IActionResult> ExportCsv(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] MoneyOperationSource? source,
        [FromQuery] MoneyOperationDirection? direction,
        [FromQuery] MoneyOperationAccountingType? accountingType,
        [FromQuery] int? moneyAccountId = null,
        [FromQuery] string? accountName = null,
        [FromQuery] string? categoryName = null,
        [FromQuery] int? linkedInvoiceId = null,
        [FromQuery] string? search = null,
        CancellationToken ct = default)
    {
        var visibility = await ResolveMoneyVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        if (!await CanExportMoneyOperationsAsync(ct))
        {
            return Forbid();
        }

        var csv = await SqliteRetryHelper.ExecuteAsync(
            async cancellationToken =>
            {
                var rows = await ApplyMoneyOperationVisibility(
                        BuildMoneyOperationsQuery(from, to, source, direction, accountingType, moneyAccountId, accountName, categoryName, search),
                        visibility)
                    .Where(item => !linkedInvoiceId.HasValue || item.LinkedInvoiceId == linkedInvoiceId.Value)
                    .OrderByDescending(item => item.Date)
                    .ThenByDescending(item => item.Id)
                    .ToListAsync(cancellationToken);

                return BuildMoneyOperationsCsv(rows.Select(MapMoneyOperationRow));
            },
            _logger,
            ct);

        var content = Encoding.UTF8.GetBytes(csv);
        var preamble = Encoding.UTF8.GetPreamble();
        var bytes = new byte[preamble.Length + content.Length];
        Buffer.BlockCopy(preamble, 0, bytes, 0, preamble.Length);
        Buffer.BlockCopy(content, 0, bytes, preamble.Length, content.Length);

        return File(bytes, "text/csv; charset=utf-8", $"money-operations-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
    }

    [HttpGet("export.xls")]
    public async Task<IActionResult> ExportExcel(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] MoneyOperationSource? source,
        [FromQuery] MoneyOperationDirection? direction,
        [FromQuery] MoneyOperationAccountingType? accountingType,
        [FromQuery] int? moneyAccountId = null,
        [FromQuery] string? accountName = null,
        [FromQuery] string? categoryName = null,
        [FromQuery] int? linkedInvoiceId = null,
        [FromQuery] string? search = null,
        CancellationToken ct = default)
    {
        var visibility = await ResolveMoneyVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        if (!await CanExportMoneyOperationsAsync(ct))
        {
            return Forbid();
        }

        var xml = await SqliteRetryHelper.ExecuteAsync(
            async cancellationToken =>
            {
                var rows = await ApplyMoneyOperationVisibility(
                        BuildMoneyOperationsQuery(from, to, source, direction, accountingType, moneyAccountId, accountName, categoryName, search),
                        visibility)
                    .Where(item => !linkedInvoiceId.HasValue || item.LinkedInvoiceId == linkedInvoiceId.Value)
                    .OrderByDescending(item => item.Date)
                    .ThenByDescending(item => item.Id)
                    .ToListAsync(cancellationToken);

                return BuildMoneyOperationsExcelXml(rows.Select(MapMoneyOperationRow));
            },
            _logger,
            ct);

        return File(
            Encoding.UTF8.GetBytes(xml),
            "application/vnd.ms-excel; charset=utf-8",
            $"money-operations-{DateTime.UtcNow:yyyyMMdd-HHmmss}.xls");
    }

    [HttpGet("print")]
    public async Task<IActionResult> Print(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] MoneyOperationSource? source,
        [FromQuery] MoneyOperationDirection? direction,
        [FromQuery] MoneyOperationAccountingType? accountingType,
        [FromQuery] int? moneyAccountId = null,
        [FromQuery] string? accountName = null,
        [FromQuery] string? categoryName = null,
        [FromQuery] int? linkedInvoiceId = null,
        [FromQuery] string? search = null,
        CancellationToken ct = default)
    {
        var visibility = await ResolveMoneyVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        if (!await CanExportMoneyOperationsAsync(ct))
        {
            return Forbid();
        }

        var items = await SqliteRetryHelper.ExecuteAsync(
            async cancellationToken =>
            {
                var rows = await ApplyMoneyOperationVisibility(
                        BuildMoneyOperationsQuery(from, to, source, direction, accountingType, moneyAccountId, accountName, categoryName, search),
                        visibility)
                    .Where(item => !linkedInvoiceId.HasValue || item.LinkedInvoiceId == linkedInvoiceId.Value)
                    .OrderByDescending(item => item.Date)
                    .ThenByDescending(item => item.Id)
                    .ToListAsync(cancellationToken);

                return rows.Select(MapMoneyOperationRow).ToList();
            },
            _logger,
            ct);

        return Content(
            BuildMoneyOperationsPrintHtml(items, from, to, source),
            "text/html; charset=utf-8",
            Encoding.UTF8);
    }

    [HttpGet("export.pdf")]
    public async Task<IActionResult> ExportPdf(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] MoneyOperationSource? source,
        [FromQuery] MoneyOperationDirection? direction,
        [FromQuery] MoneyOperationAccountingType? accountingType,
        [FromQuery] int? moneyAccountId = null,
        [FromQuery] string? accountName = null,
        [FromQuery] string? categoryName = null,
        [FromQuery] int? linkedInvoiceId = null,
        [FromQuery] string? search = null,
        CancellationToken ct = default)
    {
        var visibility = await ResolveMoneyVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        if (!await CanExportMoneyOperationsAsync(ct))
        {
            return Forbid();
        }

        var items = await SqliteRetryHelper.ExecuteAsync(
            async cancellationToken =>
            {
                var rows = await ApplyMoneyOperationVisibility(
                        BuildMoneyOperationsQuery(from, to, source, direction, accountingType, moneyAccountId, accountName, categoryName, search),
                        visibility)
                    .Where(item => !linkedInvoiceId.HasValue || item.LinkedInvoiceId == linkedInvoiceId.Value)
                    .OrderByDescending(item => item.Date)
                    .ThenByDescending(item => item.Id)
                    .ToListAsync(cancellationToken);

                return rows.Select(MapMoneyOperationRow).ToList();
            },
            _logger,
            ct);

        try
        {
            var pdfBytes = await RenderPdfAsync(BuildMoneyOperationsPrintHtml(items, from, to, source), ct);
            return File(pdfBytes, "application/pdf", $"money-operations-{DateTime.UtcNow:yyyyMMdd-HHmmss}.pdf");
        }
        catch (InvalidOperationException ex)
        {
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, detail: ex.Message);
        }
    }

    [HttpGet("accounts")]
    public async Task<ActionResult<IReadOnlyList<string>>> GetAccounts(
        [FromQuery] MoneyOperationSource? source,
        CancellationToken ct = default)
    {
        var visibility = await ResolveMoneyVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var accounts = await SqliteRetryHelper.ExecuteAsync(
            async cancellationToken => await ApplyMoneyOperationVisibility(
                    BuildMoneyOperationsQuery(null, null, source, null, null, null, null, null, null),
                    visibility)
                .Where(item => item.AccountName != null && item.AccountName != "")
                .Select(item => item.AccountName!)
                .Distinct()
                .OrderBy(account => account)
                .Take(200)
                .ToListAsync(cancellationToken),
            _logger,
            ct);

        return Ok(accounts);
    }

    [HttpGet("account-summaries")]
    public async Task<ActionResult<IReadOnlyList<MoneyOperationAccountSummaryDto>>> GetAccountSummaries(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] MoneyOperationSource? source,
        CancellationToken ct = default)
    {
        var visibility = await ResolveMoneyVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var summaries = await SqliteRetryHelper.ExecuteAsync(async cancellationToken =>
        {
            var openingQuery = from.HasValue
                ? ApplyMoneyOperationVisibility(
                    BuildMoneyOperationsQuery(null, from.Value.Date.AddDays(-1), source, null, MoneyOperationAccountingType.Fact, null, null, null, null),
                    visibility)
                    .Where(item => item.AccountName != null && item.AccountName != "")
                : null;

            var openingRows = new List<MoneyOperationAccountSummaryDto>();
            if (openingQuery is not null)
            {
                var openingSummaryRows = await openingQuery
                    .GroupBy(item => new { item.Source, item.AccountName })
                    .Select(group => new MoneyOperationAccountSummaryQueryRow
                    {
                        Source = group.Key.Source,
                        AccountName = group.Key.AccountName!,
                        StartBalance = 0m,
                        Income = group
                            .Where(item => item.Direction == MoneyOperationDirection.Income.ToString())
                            .Sum(item => item.Amount),
                        Expense = group
                            .Where(item => item.Direction == MoneyOperationDirection.Expense.ToString())
                            .Sum(item => item.Amount),
                        EndBalance =
                            group.Where(item => item.Direction == MoneyOperationDirection.Income.ToString()).Sum(item => item.Amount) -
                            group.Where(item => item.Direction == MoneyOperationDirection.Expense.ToString()).Sum(item => item.Amount),
                    })
                    .ToListAsync(cancellationToken);
                openingRows = openingSummaryRows.Select(MapAccountSummaryRow).ToList();
            }

            var periodRows = await ApplyMoneyOperationVisibility(
                    BuildMoneyOperationsQuery(from, to, source, null, MoneyOperationAccountingType.Fact, null, null, null, null),
                    visibility)
                .Where(item => item.AccountName != null && item.AccountName != "")
                .GroupBy(item => new { item.Source, item.AccountName })
                .Select(group => new MoneyOperationAccountSummaryQueryRow
                {
                    Source = group.Key.Source,
                    AccountName = group.Key.AccountName!,
                    StartBalance = 0m,
                    Income = group
                        .Where(item => item.Direction == MoneyOperationDirection.Income.ToString())
                        .Sum(item => item.Amount),
                    Expense = group
                        .Where(item => item.Direction == MoneyOperationDirection.Expense.ToString())
                        .Sum(item => item.Amount),
                    EndBalance =
                        group.Where(item => item.Direction == MoneyOperationDirection.Income.ToString()).Sum(item => item.Amount) -
                        group.Where(item => item.Direction == MoneyOperationDirection.Expense.ToString()).Sum(item => item.Amount),
                    IncomeCount = group.Count(item => item.Direction == MoneyOperationDirection.Income.ToString()),
                    ExpenseCount = group.Count(item => item.Direction == MoneyOperationDirection.Expense.ToString()),
                })
                .ToListAsync(cancellationToken);
            var periodSummaries = periodRows.Select(MapAccountSummaryRow).ToList();

            var keys = openingRows
                .Select(row => new { row.Source, row.AccountName })
                .Concat(periodSummaries.Select(row => new { row.Source, row.AccountName }))
                .Distinct()
                .OrderBy(row => row.AccountName)
                .ThenBy(row => row.Source)
                .ToList();

            return keys
                .Select(key =>
                {
                    var opening = openingRows.FirstOrDefault(row => row.Source == key.Source && row.AccountName == key.AccountName);
                    var period = periodSummaries.FirstOrDefault(row => row.Source == key.Source && row.AccountName == key.AccountName);
                    var startBalance = opening?.EndBalance ?? 0m;
                    var income = period?.Income ?? 0m;
                    var expense = period?.Expense ?? 0m;

                    return new MoneyOperationAccountSummaryDto
                    {
                        Source = key.Source,
                        AccountName = key.AccountName,
                        StartBalance = startBalance,
                        Income = income,
                        Expense = expense,
                        EndBalance = startBalance + income - expense,
                        IncomeCount = period?.IncomeCount ?? 0,
                        ExpenseCount = period?.ExpenseCount ?? 0,
                    };
                })
                .ToList();
        }, _logger, ct);

        return Ok(summaries);
    }

    [HttpGet("daily-balances")]
    public async Task<ActionResult<IReadOnlyList<MoneyOperationDailyBalanceDto>>> GetDailyBalances(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] MoneyOperationSource? source,
        [FromQuery] string? accountName,
        CancellationToken ct = default)
    {
        var visibility = await ResolveMoneyVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        try
        {
            var balances = await SqliteRetryHelper.ExecuteAsync(
                cancellationToken => BuildDailyBalanceRowsAsync(from, to, source, accountName, visibility, cancellationToken),
                _logger, ct);
            return Ok(balances);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpGet("balance-reconciliation")]
    public async Task<ActionResult<IReadOnlyList<MoneyBalanceReconciliationResultDto>>> GetBalanceReconciliationResults(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] MoneyOperationSource? source,
        [FromQuery] string? accountName,
        CancellationToken ct = default)
    {
        if (await RequirePaymentsPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        // Saved reconciliations contain the full account balance, not just the
        // operations visible to a particular employee.
        var visibility = await ResolveMoneyVisibilityAsync(ct);
        if (visibility is null || !visibility.SeesAll)
        {
            return Forbid();
        }

        var results = await SqliteRetryHelper.ExecuteAsync(async cancellationToken =>
        {
            var scope = await MoneyReconciliationScope.ForUserAsync(_db, moneyAccess!, cancellationToken);
            var query = scope.ReadResults(_db);
            if (from.HasValue)
            {
                query = query.Where(item => item.Date >= from.Value.Date);
            }

            if (to.HasValue)
            {
                query = query.Where(item => item.Date <= to.Value.Date);
            }

            if (source.HasValue)
            {
                query = query.Where(item => item.Source == source.Value);
            }

            if (!string.IsNullOrWhiteSpace(accountName))
            {
                var normalizedAccountName = accountName.Trim();
                query = query.Where(item => item.AccountName == normalizedAccountName);
            }

            var rows = await query
                .OrderBy(item => item.Date)
                .ThenBy(item => item.AccountName)
                .ThenBy(item => item.Source)
                .ToListAsync(cancellationToken);

            return rows.Select(MapBalanceReconciliationResult).ToList();
        }, _logger, ct);

        return Ok(results);
    }

    [HttpPost("balance-reconciliation/run")]
    public async Task<ActionResult<MoneyBalanceReconciliationRunResultDto>> RunBalanceReconciliation(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] MoneyOperationSource? source,
        [FromQuery] string? accountName,
        CancellationToken ct = default)
    {
        if (await RequirePaymentsPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveMoneyVisibilityAsync(ct);
        if (visibility is null || !visibility.SeesAll)
        {
            return Forbid();
        }

        var scope = await MoneyReconciliationScope.ForUserAsync(_db, moneyAccess!, ct);
        try
        {
            var service = new MoneyBalanceReconciliationService(_db, _logger);
            var result = await service.RunAsync(scope, from, to, source, accountName,
                async (authorityDb, token) =>
                {
                    var access = await BankStatementImportAccess.ResolveAsync(authorityDb, User, token);
                    if (access is null || !access.Permission.CanView || !access.Permission.CanEdit || !access.Visibility.SeesAll)
                        return false;
                    return scope.SameAuthority(await MoneyReconciliationScope.ForUserAsync(authorityDb, access, token));
                }, ct);
            return Ok(result);
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    private Task<List<MoneyOperationDailyBalanceDto>> BuildDailyBalanceRowsAsync(
        DateTime? from,
        DateTime? to,
        MoneyOperationSource? source,
        string? accountName,
        DataVisibilityAccess visibility,
        CancellationToken cancellationToken)
        => CreateMoneyJournalQuery().BuildDailyBalanceRowsAsync(from, to, source, accountName, visibility, cancellationToken);

    private MoneyJournalQuery CreateMoneyJournalQuery()
        => new(_db, new MoneyJournalQuerySources(
            ScopeCompanyMoneyOperations(_db.MoneyOperations.AsNoTracking()),
            ScopeCompanyPaymentOperations(_db.Payments.AsNoTracking()),
            ReadableMoneyInvoiceOperations(),
            moneyAccess?.Visibility.SeesAll != true,
            ReadableMoneyContracts(),
            ReadableMoneyActs()));

    private IQueryable<MoneyOperationQueryRow> BuildMoneyOperationsQuery(
        DateTime? from,
        DateTime? to,
        MoneyOperationSource? source,
        MoneyOperationDirection? direction,
        MoneyOperationAccountingType? accountingType,
        int? moneyAccountId,
        string? accountName,
        string? categoryName,
        string? search)
        => CreateMoneyJournalQuery().Build(from, to, source, direction, accountingType, moneyAccountId, accountName, categoryName, search);





    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id, CancellationToken ct)
    {
        var visibility = await ResolveMoneyVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }
        if (!await CanAccessCustomMoneyOperationAsync(id, visibility, ct))
        {
            return NotFound();
        }

        var item = await _db.MoneyOperations
            .AsNoTracking()
            .Include(operation => operation.MoneyAccount)
            .Include(operation => operation.ResponsibleUser)
            .FirstOrDefaultAsync(operation => operation.Id == id, ct);
        if (item is null) return NotFound();
        if (item.LinkedInvoiceId is int invoiceId && !await CanAccessMoneyInvoiceAsync(item, invoiceId, forWrite: false, ct))
        {
            item.IsInvoiceLinkProtected = true;
            item.LinkedInvoiceId = null;
        }
        return Ok(item);
    }

    private static string? ApplyMoneyAccount(MoneyOperation model, MoneyAccount account)
    {
        if (!string.IsNullOrWhiteSpace(model.Currency)
            && !string.Equals(model.Currency.Trim(), account.Currency, StringComparison.OrdinalIgnoreCase))
            return "Валюта операции должна совпадать с валютой денежного счёта.";
        model.MoneyAccountId = account.Id;
        model.Source = account.Source;
        model.AccountName = account.Name;
        model.Currency = account.Currency;
        return null;
    }

    private async Task<string?> ApplyIncomeTypeAsync(MoneyOperation model, CancellationToken ct)
    {
        model.CategoryName = string.IsNullOrWhiteSpace(model.CategoryName) ? null : model.CategoryName.Trim();

        var incomeTypeId = model.IncomeTypeId.GetValueOrDefault();
        if (incomeTypeId <= 0)
        {
            model.IncomeTypeId = null;
            return null;
        }

        if (model.Direction == MoneyOperationDirection.Transfer)
        {
            model.IncomeTypeId = null;
            return null;
        }

        var incomeType = await _db.IncomeTypes
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == incomeTypeId, ct);
        if (incomeType is null)
        {
            return "Статья движения денег не найдена.";
        }

        if (!incomeType.IsActive)
        {
            return "Статья движения денег отключена и не может использоваться в новой операции.";
        }

        var expectedPaymentType = model.Direction == MoneyOperationDirection.Expense
            ? PaymentType.Expense
            : PaymentType.Income;
        if (incomeType.PaymentType != expectedPaymentType)
        {
            return model.Direction == MoneyOperationDirection.Expense
                ? "Для расхода нужно выбрать статью расхода."
                : "Для прихода нужно выбрать статью прихода.";
        }

        model.CategoryName = null;
        return null;
    }

    private async Task<string?> ApplyResponsibleUserAsync(MoneyOperation model, CancellationToken ct)
    {
        var responsibleUserId = model.ResponsibleUserId.GetValueOrDefault();
        if (responsibleUserId <= 0)
        {
            model.ResponsibleUserId = null;
            return null;
        }

        var exists = await _db.Users
            .AsNoTracking()
            .AnyAsync(user => user.Id == responsibleUserId && user.IsActive, ct);
        if (!exists)
        {
            return "Ответственный за денежную операцию не найден или отключён.";
        }

        model.ResponsibleUserId = responsibleUserId;
        return null;
    }

    [HttpGet("{id:int}/invoice-candidates")]
    public async Task<ActionResult<IReadOnlyList<MoneyOperationInvoiceCandidateDto>>> GetInvoiceCandidates(
        int id,
        CancellationToken ct)
    {
        if (await RequirePaymentsPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        if (!await CanAccessCustomMoneyOperationAsync(id, moneyAccess!.Visibility, ct)) return NotFound();

        var operation = await _db.MoneyOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (operation is null)
        {
            return NotFound();
        }

        if (operation.Direction is not (MoneyOperationDirection.Income or MoneyOperationDirection.Expense))
        {
            return Ok(Array.Empty<MoneyOperationInvoiceCandidateDto>());
        }

        var invoiceAccess = await ResolveMoneyInvoiceAccessAsync(operation, forWrite: false, ct);
        if (invoiceAccess is null) return Forbid();

        var targetPaymentType = operation.Direction == MoneyOperationDirection.Expense
            ? PaymentType.Expense
            : PaymentType.Income;
        var purposeTokens = ExtractInvoiceNumberTokens(operation.Purpose)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var operationAmount = Math.Round(operation.Amount, 2, MidpointRounding.AwayFromZero);
        var operationCounterparty = operation.CounterpartyName?.Trim();

        var invoiceQuery = InvoicePaymentAccess.ScopeHeaders(_db, invoiceAccess.Section, invoiceAccess.CompanyId)
            .Include(payment => payment.Invoice)
            .Include(payment => payment.Client)
            .Include(payment => payment.ExpenseClient)
            .Include(payment => payment.ExpenseLegalEntity)
            .Include(payment => payment.Contract)
            .Where(payment =>
                payment.DocumentKind == DocumentKind.Invoice &&
                payment.Type == targetPaymentType &&
                payment.Status != PaymentStatus.Cancelled &&
                payment.InvoiceId.HasValue)
            ;

        var candidates = new List<MoneyOperationInvoiceCandidateDto>();
        var lastPaymentId = 0;
        while (true)
        {
            var invoicePayments = await invoiceQuery.Where(payment => payment.Id > lastPaymentId)
                .OrderBy(payment => payment.Id).Take(100).ToListAsync(ct);
            if (invoicePayments.Count == 0) break;
            lastPaymentId = invoicePayments[^1].Id;

            var invoiceIds = invoicePayments
                .Select(payment => payment.InvoiceId!.Value)
                .Distinct()
                .ToList();

            var aggregatePaymentsByInvoiceId = invoiceIds.Count == 0
                ? new Dictionary<int, List<Payment>>()
                : (await _db.Payments
                    .AsNoTracking()
                    .Where(payment => payment.InvoiceId.HasValue && invoiceIds.Contains(payment.InvoiceId.Value))
                    .ToListAsync(ct))
                .GroupBy(payment => payment.InvoiceId!.Value)
                .ToDictionary(group => group.Key, group => group.ToList());

            candidates = candidates.Concat(invoicePayments
                .Select(payment =>
                {
                    var invoiceNumber = payment.Invoice?.Number ?? payment.Account ?? payment.LegacyAccountNumber ?? string.Empty;
                    var invoiceNumberTokens = BuildInvoiceNumberTokenSet(payment.Invoice?.Number, payment.Account, payment.LegacyAccountNumber);
                    var contractNumberTokens = BuildInvoiceNumberTokenSet(payment.Contract?.Number);
                    var relatedPayments = aggregatePaymentsByInvoiceId.TryGetValue(payment.InvoiceId!.Value, out var aggregatePayments)
                        ? aggregatePayments
                        : new List<Payment> { payment };
                    var statusSourcePayments = InvoicePaymentStateService.SelectStatusSourcePayments(
                        relatedPayments,
                        payment.Id,
                        payment.Amount);
                    var paidAmount = statusSourcePayments.Count > 0
                        ? Math.Round(statusSourcePayments.Sum(InvoicePaymentStateService.ResolvePaidAmount), 2, MidpointRounding.AwayFromZero)
                        : InvoicePaymentStateService.ResolvePaidAmount(payment);
                    var outstandingAmount = statusSourcePayments.Count > 0
                        ? Math.Round(statusSourcePayments.Sum(InvoicePaymentStateService.ResolveOutstandingAmount), 2, MidpointRounding.AwayFromZero)
                        : CalculateOutstandingAmount(payment.Amount, payment.PaidAmount, payment.Status);
                    var score = 0;
                    var reasons = new List<string>();

                    if (purposeTokens.Count > 0 && invoiceNumberTokens.Any(purposeTokens.Contains))
                    {
                        score += 100;
                        reasons.Add("номер счёта найден в назначении");
                    }
                    else if (purposeTokens.Count > 0 && contractNumberTokens.Any(purposeTokens.Contains))
                    {
                        score += 90;
                        reasons.Add("номер договора найден в назначении");
                    }

                    if (outstandingAmount > 0m && operationAmount <= outstandingAmount + 0.009m)
                    {
                        score += 20;
                        reasons.Add("сумма помещается в остаток счёта");
                    }

                    if (Math.Abs(outstandingAmount - operationAmount) <= 0.009m)
                    {
                        score += 40;
                        reasons.Add("сумма совпадает с остатком счёта");
                    }

                    var candidateName = ResolveInvoiceCandidateCounterpartyName(payment);
                    if (NamesLookRelated(operationCounterparty, candidateName))
                    {
                        score += 20;
                        reasons.Add(targetPaymentType == PaymentType.Expense ? "похожий поставщик" : "похожий контрагент");
                    }

                    return new MoneyOperationInvoiceCandidateDto
                    {
                        InvoiceId = payment.InvoiceId!.Value,
                        PaymentId = payment.Id,
                        InvoiceNumber = invoiceNumber,
                        InvoiceDate = payment.Invoice?.Date ?? payment.AccountDate,
                        Amount = payment.Amount,
                        PaidAmount = paidAmount,
                        OutstandingAmount = outstandingAmount,
                        Status = payment.Status,
                        ClientId = targetPaymentType == PaymentType.Expense
                            ? payment.ExpenseClientId ?? payment.ClientId
                            : payment.ClientId,
                        ClientName = candidateName,
                        CounterpartyName = payment.CounterpartyName,
                        MatchReason = reasons.Count == 0 ? null : string.Join(", ", reasons),
                        Score = score,
                    };
                }))
                .Where(candidate => candidate.OutstandingAmount > 0m && candidate.Score > 0)
                .OrderByDescending(candidate => candidate.Score)
                .ThenByDescending(candidate => candidate.InvoiceDate)
                .ThenByDescending(candidate => candidate.InvoiceId)
                .Take(10)
                .ToList();

        }

        var freshAccess = await ResolveMoneyInvoiceAccessAsync(operation, forWrite: false, ct);
        if (freshAccess is null) return Forbid();
        if (moneyAccess?.Permission.CanEdit != true) return Forbid();
        var candidateIds = candidates.Select(candidate => candidate.InvoiceId).ToArray();
        int[] allowedIds = candidateIds.Length == 0 ? [] : await InvoicePaymentAccess.ScopeHeaders(_db, freshAccess.Section, freshAccess.CompanyId)
            .Where(payment => payment.Type == targetPaymentType && payment.InvoiceId.HasValue && candidateIds.Contains(payment.InvoiceId.Value))
            .Select(payment => payment.InvoiceId!.Value).Distinct().ToArrayAsync(ct);
        return Ok(candidates.Where(candidate => allowedIds.Contains(candidate.InvoiceId)).ToList());
    }

    [HttpPost("{id:int}/ignore")]
    public async Task<IActionResult> Ignore(int id, CancellationToken ct)
    {
        if (await RequirePaymentsPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        if (!await CanAccessCustomMoneyOperationAsync(id, moneyAccess!.Visibility, ct)) return NotFound();

        var operation = await _db.MoneyOperations.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (operation is null)
        {
            return NotFound();
        }

        await using var mutation = await MoneyMutationScope.BeginAsync(_db, ct);
        var lease = await LeaseMoneyAccountsAsync([operation.MoneyAccountId.GetValueOrDefault()], static permission => permission.CanEdit, ct, allowExistingBankState: true);
        if (lease.Error is not null) return lease.Error;
        operation = await LockMoneyOperationAsync(operation, ct);
        if (operation is null) return Conflict("Операция изменена другим запросом. Обновите запись.");
        if (!await CanAccessCustomMoneyOperationAsync(id, moneyAccess!.Visibility, ct)) return NotFound();

        if (IsAppliedToInvoice(operation))
        {
            return BadRequest("Связанную со счётом денежную операцию нельзя пометить как игнорируемую без операции сторно.");
        }

        operation.LinkedInvoiceId = null;
        operation.PaidInvoiceNumber = null;
        operation.PaidAccountName = null;
        operation.MatchStatus = MoneyOperationMatchStatus.Ignored;
        operation.MatchedAt = null;
        operation.MatchComment = "Операция выписки помечена как не требующая сверки.";
        operation.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        if (await RefreshMoneyMutationAccessAsync(lease.Accounts.Values, static permission => permission.CanEdit, ct) is { } finalAccess)
            return finalAccess;
        await mutation.CommitAsync(ct);
        return Ok(operation);
    }

    [HttpPost("{id:int}/link-invoice")]
    public async Task<IActionResult> LinkInvoice(
        int id,
        [FromBody] LinkMoneyOperationInvoiceRequest request,
        CancellationToken ct)
    {
        if (await RequirePaymentsPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        if (!await CanAccessCustomMoneyOperationAsync(id, moneyAccess!.Visibility, ct)) return NotFound();

        if (request.InvoiceId <= 0)
        {
            return BadRequest("Не выбран счёт для сверки.");
        }

        var operation = await _db.MoneyOperations.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (operation is null)
        {
            return NotFound();
        }

        await using var mutation = await MoneyMutationScope.BeginAsync(_db, ct);
        var lease = await LeaseMoneyAccountsAsync([operation.MoneyAccountId.GetValueOrDefault()], static permission => permission.CanEdit, ct, allowExistingBankState: true);
        if (lease.Error is not null) return lease.Error;
        operation = await LockMoneyOperationAsync(operation, ct);
        if (operation is null) return Conflict("Операция изменена другим запросом. Обновите запись.");
        if (!await CanAccessCustomMoneyOperationAsync(id, moneyAccess!.Visibility, ct)) return NotFound();

        if (!await CanAccessMoneyInvoiceAsync(operation, request.InvoiceId, forWrite: true, ct)) return Forbid();

        if (IsAppliedToInvoice(operation))
        {
            return operation.LinkedInvoiceId == request.InvoiceId
                ? Ok(operation)
                : BadRequest("Связанную со счётом денежную операцию нельзя перенести на другой счёт без операции сторно.");
        }

        var validation = await ValidateInvoiceLinkAsync(operation, request.InvoiceId, ct);
        if (validation.Error is not null)
        {
            return BadRequest(validation.Error);
        }

        var invoice = validation.Invoice!;
        operation.LinkedInvoiceId = invoice.Id;
        operation.PaidInvoiceNumber = invoice.Number;
        operation.PaidAccountName = invoice.Number;
        operation.MatchStatus = MoneyOperationMatchStatus.Matched;
        operation.MatchedAt = DateTime.UtcNow;
        operation.MatchComment = string.IsNullOrWhiteSpace(request.Comment)
            ? $"Денежная операция сверена со счётом {invoice.Number}."
            : request.Comment.Trim();
        operation.UpdatedAt = DateTime.UtcNow;

        await ApplyMoneyOperationToInvoicePaymentAsync(operation, invoice.Id, ct);
        await _db.SaveChangesAsync(ct);
        if (await RefreshMoneyMutationAccessAsync(lease.Accounts.Values, static permission => permission.CanEdit, ct) is { } finalAccess)
            return finalAccess;
        if (!await CanAccessMoneyInvoiceAsync(operation, invoice.Id, forWrite: true, ct)) return Forbid();
        await mutation.CommitAsync(ct);
        return Ok(operation);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] MoneyOperation model, CancellationToken ct = default)
    {
        if (await RequirePaymentsPermissionAsync(static permission => permission.CanCreate, ct) is { } forbidden)
        {
            return forbidden;
        }

        // This endpoint creates one manual operation. Imported provenance and
        // related entity graphs belong to their own authorized server workflows.
        model.Id = 0;
        model.MoneyAccount = null;
        model.ResponsibleUser = null;
        model.IncomeType = null;
        model.LinkedInvoice = null;
        model.BankStatementImportBatch = null;
        model.BankStatementImportBatchId = null;
        model.SourceMovementKey = null;
        model.CashOrders = new List<CashOrder>();
        model.UpdatedAt = null;
        model.MatchedAt = null;

        if (model.Amount <= 0)
        {
            return BadRequest("Сумма денежной операции должна быть больше нуля.");
        }

        await using var mutation = await MoneyMutationScope.BeginAsync(_db, ct);
        var lease = await LeaseMoneyAccountsAsync([model.MoneyAccountId.GetValueOrDefault()], static permission => permission.CanCreate, ct);
        if (lease.Error is not null) return lease.Error;
        var moneyAccountError = ApplyMoneyAccount(model, lease.Accounts[model.MoneyAccountId!.Value]);
        if (moneyAccountError is not null)
        {
            return BadRequest(moneyAccountError);
        }

        var incomeTypeError = await ApplyIncomeTypeAsync(model, ct);
        if (incomeTypeError is not null)
        {
            return BadRequest(incomeTypeError);
        }

        var responsibleUserError = await ApplyResponsibleUserAsync(model, ct);
        if (responsibleUserError is not null)
        {
            return BadRequest(responsibleUserError);
        }

        model.AccountName = string.IsNullOrWhiteSpace(model.AccountName) ? null : model.AccountName.Trim();
        model.CounterpartyName = string.IsNullOrWhiteSpace(model.CounterpartyName) ? null : model.CounterpartyName.Trim();
        ApplyVat(model);
        model.Currency = string.IsNullOrWhiteSpace(model.Currency) ? null : model.Currency.Trim();
        model.PaymentOrderNumber = string.IsNullOrWhiteSpace(model.PaymentOrderNumber) ? null : model.PaymentOrderNumber.Trim();
        model.Purpose = string.IsNullOrWhiteSpace(model.Purpose) ? null : model.Purpose.Trim();
        model.PaidInvoiceNumber = string.IsNullOrWhiteSpace(model.PaidInvoiceNumber) ? null : model.PaidInvoiceNumber.Trim();
        model.ProjectName = string.IsNullOrWhiteSpace(model.ProjectName) ? null : model.ProjectName.Trim();
        model.DirectionName = string.IsNullOrWhiteSpace(model.DirectionName) ? null : model.DirectionName.Trim();
        model.SalesPointName = string.IsNullOrWhiteSpace(model.SalesPointName) ? null : model.SalesPointName.Trim();
        model.PaidAccountName = string.IsNullOrWhiteSpace(model.PaidAccountName) ? null : model.PaidAccountName.Trim();
        model.MatchComment = string.IsNullOrWhiteSpace(model.MatchComment) ? null : model.MatchComment.Trim();
        if (model.LinkedInvoiceId.HasValue)
        {
            var validation = await ValidateInvoiceLinkAsync(model, model.LinkedInvoiceId.Value, ct);
            var invoice = validation.Invoice;
            if (validation.Error is not null)
            {
                return BadRequest(validation.Error);
            }

            model.PaidInvoiceNumber = invoice!.Number;
            model.PaidAccountName = invoice.Number;
            model.MatchStatus = MoneyOperationMatchStatus.Matched;
            model.MatchedAt = DateTime.UtcNow;
        }
        else if (model.MatchStatus == MoneyOperationMatchStatus.Matched)
        {
            model.MatchStatus = MoneyOperationMatchStatus.None;
            model.MatchedAt = null;
        }
        ApplyAdvanceReviewHint(model);
        model.CreatedAt = DateTime.UtcNow;

        if (await RefreshMoneyMutationAccessAsync(lease.Accounts.Values, static permission => permission.CanCreate, ct) is { } changedAccess)
            return changedAccess;
        _db.MoneyOperations.Add(model);
        await _db.SaveChangesAsync(ct);
        if (model.LinkedInvoiceId.HasValue && model.MatchStatus == MoneyOperationMatchStatus.Matched)
        {
            await ApplyMoneyOperationToInvoicePaymentAsync(model, model.LinkedInvoiceId.Value, ct);
            await _db.SaveChangesAsync(ct);
        }
        await SyncCashOrderAsync(model);
        if (await RefreshMoneyMutationAccessAsync(lease.Accounts.Values, static permission => permission.CanCreate, ct) is { } finalAccess)
            return finalAccess;
        if (model.LinkedInvoiceId.HasValue && !await CanAccessMoneyInvoiceAsync(model, model.LinkedInvoiceId.Value, forWrite: true, ct)) return Forbid();
        await mutation.CommitAsync(ct);
        return Created($"/api/money-operations/{model.Id}", model);
    }

    [HttpPost("transfer")]
    public async Task<ActionResult<MoneyOperationTransferResponse>> CreateTransfer(
        [FromBody] MoneyOperationTransferRequest request,
        CancellationToken ct = default)
    {
        if (await RequirePaymentsPermissionAsync(static permission => permission.CanCreate, ct) is { } forbidden)
        {
            return forbidden;
        }

        if (request.Amount <= 0)
        {
            return BadRequest("Сумма перевода должна быть больше нуля.");
        }

        if (request.SourceMoneyAccountId <= 0 || request.TargetMoneyAccountId <= 0)
        {
            return BadRequest("Выберите счёт списания и счёт зачисления.");
        }

        if (request.SourceMoneyAccountId == request.TargetMoneyAccountId)
        {
            return BadRequest("Для перевода выберите два разных денежных счёта.");
        }

        await using var mutation = await MoneyMutationScope.BeginAsync(_db, ct);
        var lease = await LeaseMoneyAccountsAsync([request.SourceMoneyAccountId, request.TargetMoneyAccountId], static permission => permission.CanCreate, ct);
        if (lease.Error is not null) return lease.Error;
        var accounts = lease.Accounts.Values;
        var sourceAccount = accounts.FirstOrDefault(account => account.Id == request.SourceMoneyAccountId);
        var targetAccount = accounts.FirstOrDefault(account => account.Id == request.TargetMoneyAccountId);

        if (sourceAccount is null || targetAccount is null)
        {
            return BadRequest("Денежный счёт для перевода не найден.");
        }

        if (!sourceAccount.IsActive || !targetAccount.IsActive)
        {
            return BadRequest("Перевод можно создать только между активными денежными счетами.");
        }

        var now = DateTime.UtcNow;
        var currency = string.IsNullOrWhiteSpace(request.Currency)
            ? sourceAccount.Currency
            : request.Currency.Trim();
        if (!string.Equals(currency, sourceAccount.Currency, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(currency, targetAccount.Currency, StringComparison.OrdinalIgnoreCase))
            return BadRequest("Для перевода нужны счета одной валюты. Валюта перевода должна совпадать с валютой обоих счетов.");
        currency = sourceAccount.Currency;
        var date = request.Date == default
            ? await ResolveMoneyOperationTodayAsync(ct)
            : request.Date.Date;
        var purpose = string.IsNullOrWhiteSpace(request.Purpose)
            ? $"Перевод со счёта {sourceAccount.Name} на счёт {targetAccount.Name}"
            : request.Purpose.Trim();
        var paymentOrderNumber = string.IsNullOrWhiteSpace(request.PaymentOrderNumber)
            ? null
            : request.PaymentOrderNumber.Trim();

        var sourceOperation = new MoneyOperation
        {
            Source = sourceAccount.Source,
            Direction = MoneyOperationDirection.Expense,
            AccountingType = MoneyOperationAccountingType.Fact,
            MoneyAccountId = sourceAccount.Id,
            AccountName = sourceAccount.Name,
            CounterpartyName = targetAccount.Name,
            ResponsibleUserId = request.ResponsibleUserId,
            Amount = request.Amount,
            Currency = currency,
            PaymentOrderNumber = paymentOrderNumber,
            Date = date,
            RecognitionDate = request.RecognitionDate,
            IsAdvancePayment = false,
            Purpose = purpose,
            DirectionName = "Перевод между счетами",
            PaidAccountName = targetAccount.Name,
            MatchStatus = MoneyOperationMatchStatus.Ignored,
            MatchComment = $"Списание по переводу на счёт {targetAccount.Name}.",
            CreatedAt = now,
        };

        var targetOperation = new MoneyOperation
        {
            Source = targetAccount.Source,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            MoneyAccountId = targetAccount.Id,
            AccountName = targetAccount.Name,
            CounterpartyName = sourceAccount.Name,
            ResponsibleUserId = request.ResponsibleUserId,
            Amount = request.Amount,
            Currency = currency,
            PaymentOrderNumber = paymentOrderNumber,
            Date = date,
            RecognitionDate = request.RecognitionDate,
            IsAdvancePayment = false,
            Purpose = purpose,
            DirectionName = "Перевод между счетами",
            PaidAccountName = sourceAccount.Name,
            MatchStatus = MoneyOperationMatchStatus.Ignored,
            MatchComment = $"Зачисление по переводу со счёта {sourceAccount.Name}.",
            CreatedAt = now,
        };

        var responsibleError = await ApplyResponsibleUserAsync(sourceOperation, ct)
            ?? await ApplyResponsibleUserAsync(targetOperation, ct);
        if (responsibleError is not null)
        {
            return BadRequest(responsibleError);
        }

        if (await RefreshMoneyMutationAccessAsync(accounts, static permission => permission.CanCreate, ct) is { } changedAccess)
            return changedAccess;
        _db.MoneyOperations.AddRange(sourceOperation, targetOperation);
        await _db.SaveChangesAsync(ct);
        await SyncCashOrderAsync(sourceOperation);
        await SyncCashOrderAsync(targetOperation);
        if (await RefreshMoneyMutationAccessAsync(accounts, static permission => permission.CanCreate, ct) is { } finalAccess)
            return finalAccess;
        await mutation.CommitAsync(ct);

        return Created(
            $"/api/money-operations/{sourceOperation.Id}",
            new MoneyOperationTransferResponse
            {
                SourceOperation = sourceOperation,
                TargetOperation = targetOperation,
            });
    }

    private async Task<DateTime> ResolveMoneyOperationTodayAsync(CancellationToken ct)
    {
        var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct);
        return CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date;
    }

    [HttpPost("bank-statement/import")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxStatementFileSizeBytes)]
    public Task<ActionResult<BankStatementImportResultDto>> ImportBankStatement(
        IFormFile? file, CancellationToken ct, [FromForm] int? companyId = null, [FromForm] int? moneyAccountId = null)
        => ImportBankStatementFileAsync(file, companyId, moneyAccountId, BankStatementImportTriggers.Manual, ct);

    private async Task<ActionResult<BankStatementImportResultDto>> ImportBankStatementFileAsync(
        IFormFile? file, int? companyId, int? moneyAccountId, string trigger, CancellationToken ct)
    {
        var imports = new BankStatementImportService(_db);
        var preparation = await imports.PrepareFileAsync(User, companyId, moneyAccountId, trigger, ct);
        if (preparation.Status == 403) return Forbid();
        if (preparation.Status == 404) return NotFound();
        if (preparation.Status != 200) return BadRequest(preparation.Error);
        var context = preparation.Context!;

        if (file is null || file.Length == 0)
        {
            return BadRequest("Файл выписки не выбран.");
        }

        if (file.Length > MaxStatementFileSizeBytes)
        {
            return BadRequest("Файл выписки слишком большой.");
        }

        if (!IsSupportedStatementFileName(file.FileName))
        {
            return BadRequest("Поддерживаются файлы CSV, TSV, TXT и 1C ClientBankExchange.");
        }

        await using var stream = file.OpenReadStream();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, ct);
        var statementBytes = memory.ToArray();
        var fileHash = Convert.ToHexString(SHA256.HashData(statementBytes)).ToLowerInvariant();
        var text = DecodeStatementBytes(statementBytes);
        var manualCategoryRules = await GetBankStatementManualCategoryRulesAsync(GetCurrentUserId(), ct);
        if (LooksLikeClientBankExchange(text))
        {
            var clientBankResult = ParseClientBankExchange(text);
            return Ok(await imports.ImportAsync(context,
                clientBankResult.Items,
                clientBankResult.Result,
                file.FileName,
                statementBytes.Length,
                fileHash,
                manualCategoryRules,
                ct));
        }

        var rows = ParseDelimitedRows(text);
        var result = new BankStatementImportResultDto();

        if (rows.Count == 0)
        {
            return Ok(await imports.ImportAsync(context, Array.Empty<BankStatementImportItem>(), result,
                file.FileName, statementBytes.Length, fileHash, manualCategoryRules, ct));
        }

        var header = rows[0];
        var map = BuildStatementHeaderMap(header);
        if (map.Date < 0 || (map.Amount < 0 && (map.Income < 0 || map.Expense < 0)))
        {
            return BadRequest("Не найдены обязательные колонки выписки: дата и сумма либо приход/расход.");
        }

        var importItems = new List<BankStatementImportItem>();

        for (var index = 1; index < rows.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var row = rows[index];
            if (row.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            result.RowsRead++;
            var rowNumber = index + 1;

            if (!TryGetCell(row, map.Date, out var dateValue) || !TryParseStatementDate(dateValue, out var date))
            {
                result.Skipped++;
                result.Errors.Add($"Строка {rowNumber}: не удалось прочитать дату.");
                continue;
            }

            var direction = MoneyOperationDirection.Income;
            decimal amount;
            if (map.Amount >= 0 && TryGetCell(row, map.Amount, out var amountValue) && TryParseStatementAmount(amountValue, out var signedAmount))
            {
                direction = signedAmount < 0 ? MoneyOperationDirection.Expense : MoneyOperationDirection.Income;
                amount = Math.Abs(signedAmount);
            }
            else
            {
                var income = map.Income >= 0 && TryGetCell(row, map.Income, out var incomeValue) && TryParseStatementAmount(incomeValue, out var parsedIncome)
                    ? parsedIncome
                    : 0m;
                var expense = map.Expense >= 0 && TryGetCell(row, map.Expense, out var expenseValue) && TryParseStatementAmount(expenseValue, out var parsedExpense)
                    ? parsedExpense
                    : 0m;

                if (income == 0m && expense == 0m)
                {
                    result.Skipped++;
                    result.Errors.Add($"Строка {rowNumber}: не удалось прочитать сумму.");
                    continue;
                }

                direction = income != 0m ? MoneyOperationDirection.Income : MoneyOperationDirection.Expense;
                amount = Math.Abs(income != 0m ? income : expense);
            }

            if (amount <= 0m)
            {
                result.Skipped++;
                result.Errors.Add($"Строка {rowNumber}: нулевая сумма пропущена.");
                continue;
            }

            var documentNumber = NormalizeStatementText(GetOptionalCell(row, map.DocumentNumber));
            var counterparty = NormalizeStatementText(GetOptionalCell(row, map.Counterparty));
            var purpose = NormalizeStatementText(GetOptionalCell(row, map.Purpose));
            var account = NormalizeStatementText(GetOptionalCell(row, map.Account)) ?? "Расчетный счет";

            importItems.Add(new BankStatementImportItem(rowNumber, date.Date, direction, amount, account, counterparty, purpose, documentNumber));
        }

        return Ok(await imports.ImportAsync(context, importItems, result, file.FileName, statementBytes.Length, fileHash, manualCategoryRules, ct));
    }

    [HttpPost("bank-statement/auto-import/run")]
    public async Task<ActionResult<BankStatementAutoImportRunResultDto>> RunBankStatementAutoImport(CancellationToken ct, [FromQuery] int? companyId = null, [FromQuery] int? moneyAccountId = null)
    {
        if (await RequirePaymentsPermissionAsync(static permission => permission.CanCreate, ct) is { } forbidden)
        {
            return forbidden;
        }

        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var settings = await GetBankStatementAutoImportSettingsAsync(userId.Value, ct);
        var checkedAt = DateTime.UtcNow;
        var runResult = new BankStatementAutoImportRunResultDto
        {
            Enabled = settings.Enabled,
            FolderPath = settings.FolderPath,
            FileMask = settings.FileMask,
            CheckedAt = checkedAt,
        };

        if (!settings.Enabled)
        {
            runResult.Errors.Add("Автоподгрузка выписок выключена.");
            return Ok(runResult);
        }

        if (string.IsNullOrWhiteSpace(settings.FolderPath))
        {
            return BadRequest("Укажите папку для автоподгрузки выписок.");
        }

        if (!Directory.Exists(settings.FolderPath))
        {
            return BadRequest("Папка для автоподгрузки выписок не найдена.");
        }

        var files = EnumerateBankStatementAutoImportFiles(settings.FolderPath, settings.FileMask)
            .Take(MaxAutoImportStatementFiles)
            .ToList();
        runResult.FilesFound = files.Count;

        foreach (var filePath in files)
        {
            ct.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(filePath);
            var fileInfo = new FileInfo(filePath);

            if (!IsSupportedStatementFileName(fileName))
            {
                AddSkippedAutoImportFile(runResult, fileName, "Формат файла не поддерживается.");
                continue;
            }

            if (fileInfo.Length <= 0 || fileInfo.Length > MaxStatementFileSizeBytes)
            {
                AddSkippedAutoImportFile(runResult, fileName, "Файл пустой или превышает допустимый размер.");
                continue;
            }

            try
            {
                await using var stream = System.IO.File.OpenRead(filePath);
                var formFile = new FormFile(stream, 0, stream.Length, "file", fileName);
                var action = await ImportBankStatementFileAsync(formFile, companyId, moneyAccountId, BankStatementImportTriggers.Folder, ct);

                if (action.Result is OkObjectResult { Value: BankStatementImportResultDto importResult })
                {
                    runResult.FilesImported++;
                    runResult.RowsRead += importResult.RowsRead;
                    runResult.Created += importResult.Created;
                    runResult.Skipped += importResult.Skipped;
                    runResult.Errors.AddRange(importResult.Errors.Select(error => $"{fileName}: {error}"));
                    runResult.Files.Add(new BankStatementAutoImportFileResultDto
                    {
                        FileName = fileName,
                        BatchId = importResult.BatchId,
                        RowsRead = importResult.RowsRead,
                        Created = importResult.Created,
                        Skipped = importResult.Skipped,
                        Errors = importResult.Errors,
                    });
                    continue;
                }

                var message = action.Result is ObjectResult objectResult && objectResult.Value is not null
                    ? objectResult.Value.ToString() ?? "Не удалось импортировать файл."
                    : "Не удалось импортировать файл.";
                AddSkippedAutoImportFile(runResult, fileName, message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AddSkippedAutoImportFile(runResult, fileName, ex.Message);
            }
        }

        settings.LastCheckedAt = checkedAt;
        await SaveBankStatementAutoImportSettingsAsync(userId.Value, settings, ct);

        return Ok(runResult);
    }

    [HttpGet("bank-statement/imports")]
    public async Task<ActionResult<PagedResponse<BankStatementImportBatchListItemDto>>> GetBankStatementImports(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        var access = await BankStatementImportAccess.ResolveAsync(_db, User, ct);
        if (access is null || !access.Permission.CanView) return Forbid();
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 5, 100);

        var query = BankStatementImportAccess.ScopeBatches(_db, _db.BankStatementImportBatches.AsNoTracking(), access)
            .OrderByDescending(batch => batch.CreatedAt).ThenByDescending(batch => batch.Id);
        var total = await query.CountAsync(ct);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(batch => new BankStatementImportBatchListItemDto
            {
                Id = batch.Id,
                CompanyId = batch.CompanyId,
                CompanyName = batch.Company != null ? batch.Company.ShortName : null,
                MoneyAccountId = batch.MoneyAccountId,
                MoneyAccountName = batch.MoneyAccount != null ? batch.MoneyAccount.Name : null,
                BankConnectionAccountId = batch.BankConnectionAccountId,
                ImportedByUserId = batch.ImportedByUserId,
                ResponsibleUserId = batch.ResponsibleUserId,
                Source = batch.Source,
                Trigger = batch.Trigger,
                ParentBatchId = batch.ParentBatchId,
                PeriodFrom = batch.PeriodFrom,
                PeriodTo = batch.PeriodTo,
                FileName = batch.FileName,
                Status = batch.Status,
                RowsRead = batch.RowsRead,
                CreatedRows = batch.CreatedRows,
                SkippedRows = batch.SkippedRows,
                ErrorRows = batch.ErrorRows,
                RollbackRows = batch.RollbackRows,
                CreatedAt = batch.CreatedAt,
                RolledBackAt = batch.RolledBackAt,
            })
            .ToListAsync(ct);

        return Ok(new PagedResponse<BankStatementImportBatchListItemDto>
        {
            Items = items,
            Total = total,
            Page = page,
            PageSize = pageSize,
        });
    }

    [HttpGet("bank-statement/imports/{batchId:int}")]
    public async Task<ActionResult<BankStatementImportBatchDetailDto>> GetBankStatementImport(int batchId, CancellationToken ct)
    {
        var access = await BankStatementImportAccess.ResolveAsync(_db, User, ct);
        if (access is null || !access.Permission.CanView) return Forbid();
        var batch = await BankStatementImportAccess.ScopeBatches(_db, _db.BankStatementImportBatches.AsNoTracking(), access)
            .Where(item => item.Id == batchId)
            .Select(item => new BankStatementImportBatchDetailDto
            {
                Id = item.Id,
                CompanyId = item.CompanyId,
                CompanyName = item.Company != null ? item.Company.ShortName : null,
                MoneyAccountId = item.MoneyAccountId,
                MoneyAccountName = item.MoneyAccount != null ? item.MoneyAccount.Name : null,
                BankConnectionAccountId = item.BankConnectionAccountId,
                ImportedByUserId = item.ImportedByUserId,
                ResponsibleUserId = item.ResponsibleUserId,
                Source = item.Source,
                Trigger = item.Trigger,
                ParentBatchId = item.ParentBatchId,
                PeriodFrom = item.PeriodFrom,
                PeriodTo = item.PeriodTo,
                FileName = item.FileName,
                FileHash = item.FileHash,
                FileSize = item.FileSize,
                Status = item.Status,
                RowsRead = item.RowsRead,
                CreatedRows = item.CreatedRows,
                SkippedRows = item.SkippedRows,
                ErrorRows = item.ErrorRows,
                RollbackRows = item.RollbackRows,
                ErrorSummary = item.ErrorSummary,
                CreatedAt = item.CreatedAt,
                RolledBackAt = item.RolledBackAt,
                Rows = item.Rows
                    .OrderBy(row => row.RowNumber)
                    .Select(row => new BankStatementImportRowDto
                    {
                        Id = row.Id,
                        RowNumber = row.RowNumber,
                        Status = row.Status,
                        Message = row.Message,
                        MoneyOperationId = row.MoneyOperationId,
                        Date = row.Date,
                        Direction = row.Direction,
                        Amount = row.Amount,
                        AccountName = row.AccountName,
                        CounterpartyName = row.CounterpartyName,
                        Purpose = row.Purpose,
                        PaymentOrderNumber = row.PaymentOrderNumber,
                        CreatedAt = row.CreatedAt,
                        RolledBackAt = row.RolledBackAt,
                    })
                    .ToList(),
            })
            .FirstOrDefaultAsync(ct);

        return batch is null ? NotFound() : Ok(batch);
    }

    [HttpPost("bank-statement/imports/{batchId:int}/rollback")]
    public async Task<ActionResult<BankStatementImportRollbackResultDto>> RollbackBankStatementImport(int batchId, CancellationToken ct)
    {
        var access = await BankStatementImportAccess.ResolveAsync(_db, User, ct);
        if (access is null || !access.Permission.CanView || !access.Permission.CanEdit) return Forbid();
        var batch = await BankStatementImportAccess.ScopeBatches(_db, _db.BankStatementImportBatches, access, forMutation: true)
            .Include(item => item.Rows)
            .FirstOrDefaultAsync(item => item.Id == batchId, ct);
        if (batch is null)
        {
            return NotFound();
        }

        if (batch.Status == BankStatementImportBatchStatus.RolledBack)
        {
            return Ok(new BankStatementImportRollbackResultDto
            {
                BatchId = batch.Id,
                Status = batch.Status,
                DeletedOperations = 0,
                SkippedRows = 0,
                Messages = ["Импорт уже был откачен."],
            });
        }

        var operationIds = batch.Rows
            .Where(row => row.MoneyOperationId.HasValue && row.Status == BankStatementImportRowStatus.Created)
            .Select(row => row.MoneyOperationId!.Value)
            .ToArray();
        var operations = await _db.MoneyOperations
            .Include(operation => operation.CashOrders)
            .Where(operation => operationIds.Contains(operation.Id))
            .ToListAsync(ct);

        var messages = new List<string>();
        var deleted = 0;
        var skipped = 0;

        foreach (var row in batch.Rows.Where(row => row.Status == BankStatementImportRowStatus.Created))
        {
            if (!row.MoneyOperationId.HasValue)
            {
                row.Status = BankStatementImportRowStatus.RollbackSkipped;
                row.Message = "Операция уже отсутствует.";
                row.RolledBackAt = DateTime.UtcNow;
                skipped++;
                continue;
            }

            var operation = operations.FirstOrDefault(item => item.Id == row.MoneyOperationId.Value);
            if (operation is null)
            {
                row.Status = BankStatementImportRowStatus.RollbackSkipped;
                row.Message = "Операция уже отсутствует.";
                row.RolledBackAt = DateTime.UtcNow;
                skipped++;
                continue;
            }

            if (operation.LinkedInvoiceId.HasValue || operation.MatchStatus == MoneyOperationMatchStatus.Matched)
            {
                row.Status = BankStatementImportRowStatus.RollbackSkipped;
                row.Message = "Операция уже сверена со счётом, автоматический откат пропущен.";
                row.RolledBackAt = DateTime.UtcNow;
                messages.Add($"Строка {row.RowNumber}: операция сверена со счётом.");
                skipped++;
                continue;
            }

            if (operation.CashOrders.Any(order => order.Status != CashOrderStatus.Cancelled))
            {
                row.Status = BankStatementImportRowStatus.RollbackSkipped;
                row.Message = "По операции есть кассовый ордер, автоматический откат пропущен.";
                row.RolledBackAt = DateTime.UtcNow;
                messages.Add($"Строка {row.RowNumber}: есть кассовый ордер.");
                skipped++;
                continue;
            }

            _db.MoneyOperations.Remove(operation);
            row.Status = BankStatementImportRowStatus.RolledBack;
            row.RolledBackAt = DateTime.UtcNow;
            deleted++;
        }

        batch.RollbackRows = deleted;
        batch.RolledBackAt = DateTime.UtcNow;
        batch.Status = skipped == 0
            ? BankStatementImportBatchStatus.RolledBack
            : BankStatementImportBatchStatus.RollbackBlocked;

        await _db.SaveChangesAsync(ct);

        return Ok(new BankStatementImportRollbackResultDto
        {
            BatchId = batch.Id,
            Status = batch.Status,
            DeletedOperations = deleted,
            SkippedRows = skipped,
            Messages = messages,
        });
    }

    [HttpPost("bank-statement/imports/{batchId:int}/reprocess")]
    public async Task<ActionResult<BankStatementImportResultDto>> ReprocessBankStatementImport(int batchId, CancellationToken ct)
    {
        var access = await BankStatementImportAccess.ResolveAsync(_db, User, ct);
        if (access is null || !access.Permission.CanView || !access.Permission.CanCreate) return Forbid();
        var batch = await BankStatementImportAccess.ScopeBatches(_db, _db.BankStatementImportBatches, access, forMutation: true)
            .Include(item => item.Rows)
                .ThenInclude(row => row.MoneyOperation)
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == batchId, ct);
        if (batch is null)
        {
            return NotFound();
        }

        var rows = batch.Rows
            .Where(row =>
                (row.Status == BankStatementImportRowStatus.Skipped ||
                 row.Status == BankStatementImportRowStatus.Error ||
                 row.Status == BankStatementImportRowStatus.RolledBack) &&
                (row.MoneyOperationId is null || row.MoneyOperation is null))
            .OrderBy(row => row.RowNumber)
            .ToList();

        var result = new BankStatementImportResultDto { RowsRead = rows.Count };
        if (rows.Count == 0)
        {
            result.Errors.Add("В batch нет строк, доступных для повторной обработки.");
            return Ok(result);
        }

        var items = rows
            .Select(row => new BankStatementImportItem(
                row.RowNumber,
                row.Date,
                row.Direction,
                row.Amount,
                row.AccountName ?? string.Empty,
                row.CounterpartyName,
                row.Purpose,
                row.PaymentOrderNumber)
            {
                OriginRowId = row.Id,
                BankStatementOperationId = row.BankStatementOperationId,
                ExternalOperationId = row.ExternalOperationId,
                MovementKey = row.MovementKey,
                IsBooked = row.IsBooked ?? true,
            })
            .ToList();

        var account = await _db.MoneyAccounts.AsNoTracking().SingleAsync(item => item.Id == batch.MoneyAccountId, ct);
        var context = BankStatementImportService.FromAuthorizedBatch(access, User, account, batch);
        return Ok(await new BankStatementImportService(_db).ImportAsync(context,
            items,
            result,
            $"{batch.FileName} (повторная обработка)",
            batch.FileSize,
            batch.FileHash,
            await GetBankStatementManualCategoryRulesAsync(GetCurrentUserId(), ct),
            ct, batch.Id, batch.RolledBackAt));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] MoneyOperation model, CancellationToken ct = default)
    {
        if (await RequirePaymentsPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = moneyAccess?.Visibility;
        if (visibility is null)
        {
            return Unauthorized();
        }
        if (!await CanAccessCustomMoneyOperationAsync(id, visibility, ct))
        {
            return NotFound();
        }

        var entity = await _db.MoneyOperations.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (entity is null)
        {
            return NotFound();
        }

        if (model.Amount <= 0)
        {
            return BadRequest("Сумма денежной операции должна быть больше нуля.");
        }

        if (!entity.MoneyAccountId.HasValue)
            return BadRequest("Принадлежность исторической операции не подтверждена. Эта запись доступна только для просмотра.");
        await using var mutation = await MoneyMutationScope.BeginAsync(_db, ct);
        var lease = await LeaseMoneyAccountsAsync([entity.MoneyAccountId.Value, model.MoneyAccountId.GetValueOrDefault()],
            static permission => permission.CanEdit, ct, allowExistingBankState: entity.MoneyAccountId == model.MoneyAccountId);
        if (lease.Error is not null) return lease.Error;
        entity = await LockMoneyOperationAsync(entity, ct);
        if (entity is null) return Conflict("Операция изменена другим запросом. Обновите запись.");
        if (!await CanAccessCustomMoneyOperationAsync(id, moneyAccess!.Visibility, ct)) return NotFound();

        var originalLinkedInvoiceId = entity.LinkedInvoiceId;
        var originalMatchStatus = entity.MatchStatus;
        var originalAmount = entity.Amount;
        // A normal read may redact the pointer. Retain the stored relationship
        // only for an unchanged status; never accept a different supplied ID.
        // The response flag is intentionally not trusted for authorization.
        var preserveInvoiceLink = originalLinkedInvoiceId.HasValue && model.MatchStatus == originalMatchStatus
            && (model.LinkedInvoiceId == originalLinkedInvoiceId
                || model.LinkedInvoiceId is null && !await CanAccessMoneyInvoiceAsync(entity, originalLinkedInvoiceId.Value, forWrite: false, ct));
        if (originalLinkedInvoiceId.HasValue)
        {
            if (model.LinkedInvoiceId != originalLinkedInvoiceId && !preserveInvoiceLink)
            {
                return BadRequest("Сверенную денежную операцию нельзя отвязать или перенести на другой счёт через редактирование.");
            }

            if (Math.Abs(originalAmount - model.Amount) > 0.009m)
            {
                return BadRequest("Сумму сверенной денежной операции нельзя изменить через редактирование.");
            }
            if (model.MatchStatus != originalMatchStatus && model.MatchStatus != MoneyOperationMatchStatus.Matched)
                return BadRequest("Изменение распределения требует отдельной операции сверки или сторно.");
        }

        var moneyAccountError = ApplyMoneyAccount(model, lease.Accounts[model.MoneyAccountId!.Value]);
        if (moneyAccountError is not null)
        {
            return BadRequest(moneyAccountError);
        }

        var imported = entity.BankStatementImportBatchId.HasValue || entity.SourceMovementKey is not null;
        var changesMoney = MoneyFactsChanged(entity, model);
        if (originalLinkedInvoiceId.HasValue && changesMoney)
            return BadRequest("Финансовые данные связанной со счётом операции защищены от изменения через редактирование.");
        if (imported && changesMoney)
            return BadRequest("Финансовые данные импортированной операции защищены от изменения. Можно изменить её описание или категорию.");
        if (changesMoney && !await CompanyMoneyAccounts(moneyAccess!, forNewOperations: true).AnyAsync(account => account.Id == model.MoneyAccountId, ct))
            return BadRequest("Денежный счёт недоступен для изменения финансовых данных операции.");
        if (!changesMoney) model.Date = entity.Date;

        var incomeTypeError = await ApplyIncomeTypeAsync(model, ct);
        if (incomeTypeError is not null)
        {
            return BadRequest(incomeTypeError);
        }

        var responsibleUserError = await ApplyResponsibleUserAsync(model, ct);
        if (responsibleUserError is not null)
        {
            return BadRequest(responsibleUserError);
        }

        if (await RefreshMoneyMutationAccessAsync(lease.Accounts.Values, static permission => permission.CanEdit, ct) is { } changedAccess)
            return changedAccess;
        entity.Source = model.Source;
        entity.Direction = model.Direction;
        entity.AccountingType = model.AccountingType;
        entity.MoneyAccountId = model.MoneyAccountId;
        entity.Amount = model.Amount;
        ApplyVat(model);
        entity.VatRate = model.VatRate;
        entity.VatAmount = model.VatAmount;
        entity.Currency = string.IsNullOrWhiteSpace(model.Currency) ? null : model.Currency.Trim();
        entity.AccountName = string.IsNullOrWhiteSpace(model.AccountName) ? null : model.AccountName.Trim();
        entity.CounterpartyName = string.IsNullOrWhiteSpace(model.CounterpartyName) ? null : model.CounterpartyName.Trim();
        entity.ResponsibleUserId = model.ResponsibleUserId;
        entity.IncomeTypeId = model.IncomeTypeId;
        entity.CategoryName = model.CategoryName;
        entity.PaymentOrderNumber = string.IsNullOrWhiteSpace(model.PaymentOrderNumber) ? null : model.PaymentOrderNumber.Trim();
        entity.Date = model.Date;
        entity.RecognitionDate = model.RecognitionDate;
        entity.IsAdvancePayment = model.IsAdvancePayment;
        entity.Purpose = string.IsNullOrWhiteSpace(model.Purpose) ? null : model.Purpose.Trim();
        if (!preserveInvoiceLink)
        {
            entity.PaidInvoiceNumber = string.IsNullOrWhiteSpace(model.PaidInvoiceNumber) ? null : model.PaidInvoiceNumber.Trim();
            entity.PaidAmount = model.PaidAmount;
        }
        entity.ProjectName = string.IsNullOrWhiteSpace(model.ProjectName) ? null : model.ProjectName.Trim();
        entity.DirectionName = string.IsNullOrWhiteSpace(model.DirectionName) ? null : model.DirectionName.Trim();
        entity.SalesPointName = string.IsNullOrWhiteSpace(model.SalesPointName) ? null : model.SalesPointName.Trim();
        entity.PaymentPriority = model.PaymentPriority;
        if (!preserveInvoiceLink)
            entity.PaidAccountName = string.IsNullOrWhiteSpace(model.PaidAccountName) ? null : model.PaidAccountName.Trim();
        entity.MatchComment = string.IsNullOrWhiteSpace(model.MatchComment) ? null : model.MatchComment.Trim();
        // An unchanged link retains its status, timestamps and applied amount.
        // Editing the money description is not a new document allocation.
        if (!preserveInvoiceLink && model.LinkedInvoiceId.HasValue)
        {
            var validation = await ValidateInvoiceLinkAsync(entity, model.LinkedInvoiceId.Value, ct);
            if (validation.Error is not null) return BadRequest(validation.Error);
            var invoice = validation.Invoice!;

            entity.LinkedInvoiceId = invoice.Id;
            entity.PaidInvoiceNumber = invoice.Number;
            entity.PaidAccountName = invoice.Number;
            entity.MatchStatus = MoneyOperationMatchStatus.Matched;
            entity.MatchedAt = entity.MatchedAt ?? DateTime.UtcNow;
            if (!originalLinkedInvoiceId.HasValue || originalMatchStatus != MoneyOperationMatchStatus.Matched)
            {
                await ApplyMoneyOperationToInvoicePaymentAsync(entity, invoice.Id);
            }
        }
        else if (!preserveInvoiceLink)
        {
            entity.LinkedInvoiceId = null;
            entity.MatchedAt = null;
            entity.MatchStatus = model.MatchStatus switch
            {
                MoneyOperationMatchStatus.ClarificationRequired => MoneyOperationMatchStatus.ClarificationRequired,
                MoneyOperationMatchStatus.Ignored => MoneyOperationMatchStatus.Ignored,
                _ => MoneyOperationMatchStatus.None,
            };
        }
        ApplyAdvanceReviewHint(entity);
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        await SyncCashOrderAsync(entity);
        if (await RefreshMoneyMutationAccessAsync(lease.Accounts.Values, static permission => permission.CanEdit, ct) is { } finalAccess)
            return finalAccess;
        if (!preserveInvoiceLink && entity.LinkedInvoiceId.HasValue
            && !await CanAccessMoneyInvoiceAsync(entity, entity.LinkedInvoiceId.Value, forWrite: true, ct)) return Forbid();
        await mutation.CommitAsync(ct);
        return originalLinkedInvoiceId.HasValue ? await GetById(id, ct) : Ok(entity);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        if (await RequirePaymentsPermissionAsync(static permission => permission.CanDelete, ct) is { } forbidden)
            return forbidden;
        if (!await CanAccessCustomMoneyOperationAsync(id, moneyAccess!.Visibility, ct)) return NotFound();
        var snapshot = await _db.MoneyOperations.AsNoTracking().SingleOrDefaultAsync(operation => operation.Id == id, ct);
        if (snapshot is null) return NotFound();
        if (IsAppliedToInvoice(snapshot))
            return BadRequest("Связанную со счётом денежную операцию нельзя удалить без операции сторно.");
        if (await IsImportedMoneyOperationAsync(snapshot, ct))
            return BadRequest("Операцию из выписки нельзя удалить отдельно. Используйте откат соответствующей загрузки.");

        await using var mutation = await MoneyMutationScope.BeginAsync(_db, ct);
        var lease = await LeaseMoneyAccountsAsync([snapshot.MoneyAccountId.GetValueOrDefault()], static permission => permission.CanDelete, ct);
        if (lease.Error is not null) return lease.Error;
        var entity = await LockMoneyOperationAsync(snapshot, ct);
        if (entity is null) return Conflict("Операция изменена другим запросом. Обновите запись.");
        if (!await CanAccessCustomMoneyOperationAsync(id, moneyAccess!.Visibility, ct)) return NotFound();
        var account = lease.Accounts[entity.MoneyAccountId!.Value];
        if (entity.Source != account.Source || !string.Equals(entity.Currency?.Trim(), account.Currency?.Trim(), StringComparison.OrdinalIgnoreCase))
            return BadRequest("Происхождение денежной операции требует уточнения.");
        if (IsAppliedToInvoice(entity) || await IsImportedMoneyOperationAsync(entity, ct))
            return BadRequest("Связанную или импортированную операцию нельзя удалить отдельно.");
        var linkedOrders = await _db.CashOrders
            .Where(order => order.MoneyOperationId == entity.Id && order.Status != CashOrderStatus.Cancelled).ToListAsync(ct);
        foreach (var order in linkedOrders)
        {
            order.Status = CashOrderStatus.Cancelled;
            order.UpdatedAt = DateTime.UtcNow;
        }
        _db.MoneyOperations.Remove(entity);
        await _db.SaveChangesAsync(ct);
        if (await RefreshMoneyMutationAccessAsync(lease.Accounts.Values, static permission => permission.CanDelete, ct) is { } finalAccess)
            return finalAccess;
        await mutation.CommitAsync(ct);
        return NoContent();
    }

    private Task<bool> IsImportedMoneyOperationAsync(MoneyOperation operation, CancellationToken ct)
        => operation.BankStatementImportBatchId.HasValue || operation.SourceMovementKey != null
            ? Task.FromResult(true)
            : _db.BankStatementImportRows.AnyAsync(row => row.MoneyOperationId == operation.Id, ct);

    private static bool IsAppliedToInvoice(MoneyOperation operation)
        => operation.LinkedInvoiceId.HasValue && operation.MatchStatus == MoneyOperationMatchStatus.Matched;

    private async Task SyncCashOrderAsync(MoneyOperation operation)
    {
        var existing = await _db.CashOrders
            .Where(order => order.MoneyOperationId == operation.Id && order.Status != CashOrderStatus.Cancelled)
            .OrderByDescending(order => order.Id)
            .FirstOrDefaultAsync();

        if (!ShouldHaveCashOrder(operation))
        {
            if (existing is not null)
            {
                existing.Status = CashOrderStatus.Cancelled;
                existing.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
            }

            return;
        }

        var type = operation.Direction == MoneyOperationDirection.Expense
            ? CashOrderType.Outgoing
            : CashOrderType.Incoming;

        if (existing is not null && existing.Type != type)
        {
            existing.Status = CashOrderStatus.Cancelled;
            existing.UpdatedAt = DateTime.UtcNow;
            existing = null;
        }

        if (existing is null)
        {
            _db.CashOrders.Add(new CashOrder
            {
                Number = await GenerateCashOrderNumberAsync(type, operation.Date),
                Type = type,
                Status = CashOrderStatus.Posted,
                Date = operation.Date.Date,
                Amount = operation.Amount,
                Currency = string.IsNullOrWhiteSpace(operation.Currency) ? "RUB" : operation.Currency,
                MoneyOperationId = operation.Id,
                Account = operation.AccountName,
                PayerName = operation.CounterpartyName,
                Basis = BuildCashOrderBasis(operation),
                Comment = operation.CategoryName,
                CreatedAt = DateTime.UtcNow,
            });
        }
        else
        {
            existing.Date = operation.Date.Date;
            existing.Amount = operation.Amount;
            existing.Currency = string.IsNullOrWhiteSpace(operation.Currency) ? "RUB" : operation.Currency;
            existing.Account = operation.AccountName;
            existing.PayerName = operation.CounterpartyName;
            existing.Basis = BuildCashOrderBasis(operation);
            existing.Comment = operation.CategoryName;
            existing.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
    }

    private static bool ShouldHaveCashOrder(MoneyOperation operation)
        => operation.Source == MoneyOperationSource.Cash &&
           operation.AccountingType == MoneyOperationAccountingType.Fact &&
           operation.Amount > 0m &&
           (operation.Direction == MoneyOperationDirection.Income ||
            operation.Direction == MoneyOperationDirection.Expense);

    private static void ApplyVat(MoneyOperation operation)
    {
        if (!operation.VatRate.HasValue || operation.VatRate.Value <= 0m || operation.Amount <= 0m)
        {
            operation.VatRate = operation.VatRate.HasValue && operation.VatRate.Value == 0m ? 0m : null;
            operation.VatAmount = operation.VatRate == 0m ? 0m : null;
            return;
        }

        operation.VatRate = Math.Round(operation.VatRate.Value, 2, MidpointRounding.AwayFromZero);
        operation.VatAmount = Math.Round(operation.Amount * operation.VatRate.Value / (100m + operation.VatRate.Value), 2, MidpointRounding.AwayFromZero);
    }



    private static MoneyOperationListItemDto MapMoneyOperationRow(MoneyOperationQueryRow row)
    {
        var source = ParseEnum<MoneyOperationSource>(row.Source);
        var direction = ParseEnum<MoneyOperationDirection>(row.Direction);
        var accountingType = ParseEnum<MoneyOperationAccountingType>(row.AccountingType);
        var matchStatus = string.IsNullOrWhiteSpace(row.MatchStatus)
            ? MoneyOperationMatchStatus.None
            : ParseEnum<MoneyOperationMatchStatus>(row.MatchStatus);

        return new()
        {
            Id = row.Id,
            Source = source,
            Direction = direction,
            AccountingType = accountingType,
            JournalStatus = ResolveJournalStatus(source, direction, accountingType, matchStatus, row.IsReadonly),
            MoneyAccountId = row.MoneyAccountId,
            MoneyAccountName = row.MoneyAccountName,
            AccountName = row.AccountName,
            CounterpartyName = row.CounterpartyName,
            ResponsibleUserId = row.ResponsibleUserId,
            ResponsibleName = row.ResponsibleName,
            IncomeTypeId = row.IncomeTypeId,
            CategoryName = row.CategoryName,
            Amount = row.Amount,
            VatRate = row.VatRate,
            VatAmount = row.VatAmount,
            Currency = row.Currency,
            PaymentOrderNumber = row.PaymentOrderNumber,
            Date = row.Date,
            RecognitionDate = row.RecognitionDate,
            IsAdvancePayment = row.IsAdvancePayment,
            Purpose = row.Purpose,
            PaidInvoiceNumber = row.PaidInvoiceNumber,
            PaidAmount = row.PaidAmount,
            ProjectName = row.ProjectName,
            DirectionName = row.DirectionName,
            SalesPointName = row.SalesPointName,
            PaymentPriority = row.PaymentPriority,
            PaidAccountName = row.PaidAccountName,
            LinkedInvoiceId = row.LinkedInvoiceId,
            IsInvoiceLinkProtected = row.IsInvoiceLinkProtected,
            LinkedInvoiceNumber = row.LinkedInvoiceNumber,
            LinkedInvoiceDate = row.LinkedInvoiceDate,
            LinkedContractId = row.LinkedContractId,
            LinkedContractNumber = row.LinkedContractNumber,
            LinkedActId = row.LinkedActId,
            LinkedActNumber = row.LinkedActNumber,
            LinkedActDate = row.LinkedActDate,
            MatchStatus = matchStatus,
            MatchedAt = row.MatchedAt,
            MatchComment = row.MatchComment,
            AttachmentCount = row.AttachmentCount ?? 0,
            AttachmentSummary = row.AttachmentSummary,
            CreatedAt = row.CreatedAt,
            UpdatedAt = row.UpdatedAt,
            IsReadonly = row.IsReadonly,
            RelatedPaymentId = row.RelatedPaymentId,
            CashOrderId = row.CashOrderId,
            CashOrderNumber = row.CashOrderNumber,
            CashOrderDate = row.CashOrderDate,
            CashOrderStatus = string.IsNullOrWhiteSpace(row.CashOrderStatus)
                ? null
                : ParseEnum<CashOrderStatus>(row.CashOrderStatus),
            CanCreateCashOrder = row.CanCreateCashOrder,
        };
    }

    private static MoneyBalanceReconciliationResultDto MapBalanceReconciliationResult(MoneyBalanceReconciliationResult row)
        => MoneyBalanceReconciliationService.Map(row);

    private static MoneyOperationJournalStatus ResolveJournalStatus(
        MoneyOperationSource source,
        MoneyOperationDirection direction,
        MoneyOperationAccountingType accountingType,
        MoneyOperationMatchStatus matchStatus,
        bool isReadonly)
    {
        if (matchStatus == MoneyOperationMatchStatus.ClarificationRequired)
        {
            return MoneyOperationJournalStatus.Error;
        }

        if (accountingType == MoneyOperationAccountingType.Plan)
        {
            return MoneyOperationJournalStatus.Processing;
        }

        if (!isReadonly &&
            source == MoneyOperationSource.Bank &&
            direction == MoneyOperationDirection.Income &&
            matchStatus == MoneyOperationMatchStatus.None)
        {
            return MoneyOperationJournalStatus.Processing;
        }

        return MoneyOperationJournalStatus.Posted;
    }

    private static string BuildMoneyOperationsCsv(IEnumerable<MoneyOperationListItemDto> items)
    {
        var builder = new StringBuilder();
        AppendCsvRow(builder, new[]
        {
            "Дата",
            "Источник",
            "Статус",
            "Движение",
            "Учет",
            "Счет",
            "Контрагент",
            "Ответственный",
            "Сумма",
            "Ставка НДС",
            "Сумма НДС",
            "Валюта",
            "Назначение",
            "Платежное поручение",
            "Статья",
            "Связанный счет",
            "Договор",
            "Акт",
            "Статус сверки",
            "Комментарий сверки",
            "Вложения",
        });

        foreach (var item in items)
        {
            AppendCsvRow(builder, new[]
            {
                item.Date.ToString("dd.MM.yyyy", CultureInfo.GetCultureInfo("ru-RU")),
                GetMoneyOperationSourceLabel(item.Source),
                GetJournalStatusLabel(item.JournalStatus),
                GetMoneyOperationDirectionLabel(item.Direction),
                GetMoneyOperationAccountingTypeLabel(item.AccountingType),
                item.AccountName ?? item.MoneyAccountName ?? string.Empty,
                item.CounterpartyName ?? string.Empty,
                item.ResponsibleName ?? string.Empty,
                item.Amount.ToString("0.00", CultureInfo.InvariantCulture),
                item.VatRate?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty,
                item.VatAmount?.ToString("0.00", CultureInfo.InvariantCulture) ?? string.Empty,
                item.Currency ?? string.Empty,
                item.Purpose ?? string.Empty,
                item.PaymentOrderNumber ?? string.Empty,
                item.CategoryName ?? string.Empty,
                item.LinkedInvoiceNumber ?? item.PaidInvoiceNumber ?? string.Empty,
                item.LinkedContractNumber ?? string.Empty,
                item.LinkedActNumber ?? string.Empty,
                GetMoneyOperationMatchStatusLabel(item.MatchStatus),
                item.MatchComment ?? string.Empty,
                item.AttachmentSummary ?? string.Empty,
            });
        }

        return builder.ToString();
    }

    private static void AppendCsvRow(StringBuilder builder, IReadOnlyList<string> values)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(';');
            }

            builder.Append(EscapeCsvField(values[index]));
        }

        builder.AppendLine();
    }

    private static string EscapeCsvField(string value)
    {
        var text = value.Replace("\"", "\"\"");
        return text.IndexOfAny([';', '"', '\r', '\n']) >= 0
            ? $"\"{text}\""
            : text;
    }

    private static string BuildMoneyOperationsExcelXml(IEnumerable<MoneyOperationListItemDto> items)
    {
        var builder = new StringBuilder();
        builder.AppendLine("""<?xml version="1.0" encoding="utf-8"?>""");
        builder.AppendLine("""<?mso-application progid="Excel.Sheet"?>""");
        builder.AppendLine("""
<Workbook xmlns="urn:schemas-microsoft-com:office:spreadsheet"
          xmlns:ss="urn:schemas-microsoft-com:office:spreadsheet">
  <Worksheet ss:Name="Money journal">
    <Table>
""");
        AppendExcelRow(builder, new[]
        {
            "Дата",
            "Источник",
            "Статус",
            "Движение",
            "Учет",
            "Счет",
            "Контрагент",
            "Ответственный",
            "Сумма",
            "Ставка НДС",
            "Сумма НДС",
            "Валюта",
            "Назначение",
            "Платежное поручение",
            "Статья",
            "Связанный счет",
            "Договор",
            "Акт",
            "Статус сверки",
            "Комментарий сверки",
            "Вложения",
        }, header: true);

        foreach (var item in items)
        {
            AppendExcelRow(builder, new[]
            {
                item.Date.ToString("dd.MM.yyyy", CultureInfo.GetCultureInfo("ru-RU")),
                GetMoneyOperationSourceLabel(item.Source),
                GetJournalStatusLabel(item.JournalStatus),
                GetMoneyOperationDirectionLabel(item.Direction),
                GetMoneyOperationAccountingTypeLabel(item.AccountingType),
                item.AccountName ?? item.MoneyAccountName ?? string.Empty,
                item.CounterpartyName ?? string.Empty,
                item.ResponsibleName ?? string.Empty,
                item.Amount.ToString("0.00", CultureInfo.InvariantCulture),
                item.VatRate?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty,
                item.VatAmount?.ToString("0.00", CultureInfo.InvariantCulture) ?? string.Empty,
                item.Currency ?? string.Empty,
                item.Purpose ?? string.Empty,
                item.PaymentOrderNumber ?? string.Empty,
                item.CategoryName ?? string.Empty,
                item.LinkedInvoiceNumber ?? item.PaidInvoiceNumber ?? string.Empty,
                item.LinkedContractNumber ?? string.Empty,
                item.LinkedActNumber ?? string.Empty,
                GetMoneyOperationMatchStatusLabel(item.MatchStatus),
                item.MatchComment ?? string.Empty,
                item.AttachmentSummary ?? string.Empty,
            });
        }

        builder.AppendLine("""
    </Table>
  </Worksheet>
</Workbook>
""");
        return builder.ToString();
    }

    private static void AppendExcelRow(StringBuilder builder, IReadOnlyList<string> values, bool header = false)
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

    private static string GetMoneyOperationSourceLabel(MoneyOperationSource source)
        => source switch
        {
            MoneyOperationSource.Bank => "Банк",
            MoneyOperationSource.Cash => "Касса",
            MoneyOperationSource.Card => "Карта",
            MoneyOperationSource.EMoney => "E-money",
            _ => source.ToString(),
        };

    private static string GetJournalStatusLabel(MoneyOperationJournalStatus status)
        => status switch
        {
            MoneyOperationJournalStatus.Posted => "Проведено",
            MoneyOperationJournalStatus.Processing => "В обработке",
            MoneyOperationJournalStatus.Error => "Ошибка",
            _ => status.ToString(),
        };

    private static string GetMoneyOperationDirectionLabel(MoneyOperationDirection direction)
        => direction switch
        {
            MoneyOperationDirection.Income => "Приход",
            MoneyOperationDirection.Expense => "Расход",
            MoneyOperationDirection.Transfer => "Перевод",
            _ => direction.ToString(),
        };

    private static string GetMoneyOperationAccountingTypeLabel(MoneyOperationAccountingType accountingType)
        => accountingType switch
        {
            MoneyOperationAccountingType.Fact => "Факт",
            MoneyOperationAccountingType.Plan => "План",
            _ => accountingType.ToString(),
        };

    private static string GetMoneyOperationMatchStatusLabel(MoneyOperationMatchStatus matchStatus)
        => matchStatus switch
        {
            MoneyOperationMatchStatus.Matched => "Сверено",
            MoneyOperationMatchStatus.ClarificationRequired => "Требует уточнения",
            MoneyOperationMatchStatus.Ignored => "Игнорируется",
            MoneyOperationMatchStatus.None => "Не сверено",
            _ => matchStatus.ToString(),
        };

    private static string BuildMoneyOperationsPrintHtml(
        IReadOnlyCollection<MoneyOperationListItemDto> items,
        DateTime? from,
        DateTime? to,
        MoneyOperationSource? source)
    {
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var fromText = from?.Date.ToString("dd.MM.yyyy", ru) ?? "не задано";
        var toText = to?.Date.ToString("dd.MM.yyyy", ru) ?? "не задано";
        var sourceText = source.HasValue ? GetMoneyOperationSourceLabel(source.Value) : "Все источники";
        var factItems = items.Where(item => item.AccountingType == MoneyOperationAccountingType.Fact).ToList();
        var income = factItems
            .Where(item => item.Direction == MoneyOperationDirection.Income)
            .Sum(item => item.Amount);
        var expense = factItems
            .Where(item => item.Direction == MoneyOperationDirection.Expense)
            .Sum(item => item.Amount);

        var rows = new StringBuilder();
        foreach (var item in items)
        {
            rows.AppendLine($"""
              <tr>
                <td>{item.Date.ToString("dd.MM.yyyy", ru)}</td>
                <td>{Html(GetMoneyOperationSourceLabel(item.Source))}</td>
                <td>{Html(GetJournalStatusLabel(item.JournalStatus))}</td>
                <td>{Html(GetMoneyOperationDirectionLabel(item.Direction))}</td>
                <td>{Html(GetMoneyOperationAccountingTypeLabel(item.AccountingType))}</td>
                <td>{Html(item.AccountName ?? item.MoneyAccountName ?? "-")}</td>
                <td>{Html(item.CounterpartyName ?? "-")}</td>
                <td>{Html(item.ResponsibleName ?? "-")}</td>
                <td class="num">{Html(item.Amount.ToString("N2", ru))}</td>
                <td class="num">{Html(item.VatRate.HasValue ? item.VatRate.Value.ToString("N2", ru) : "-")}</td>
                <td class="num">{Html(item.VatAmount.HasValue ? item.VatAmount.Value.ToString("N2", ru) : "-")}</td>
                <td>{Html(item.Currency ?? "-")}</td>
                <td>{Html(item.Purpose ?? "-")}</td>
                <td>{Html(item.PaymentOrderNumber ?? "-")}</td>
                <td>{Html(item.LinkedInvoiceNumber ?? item.PaidInvoiceNumber ?? "-")}</td>
                <td>{Html(item.LinkedContractNumber ?? "-")}</td>
                <td>{Html(item.LinkedActNumber ?? "-")}</td>
                <td>{Html(GetMoneyOperationMatchStatusLabel(item.MatchStatus))}</td>
                <td>{Html(item.AttachmentSummary ?? "-")}</td>
              </tr>
            """);
        }

        var emptyRow = items.Count == 0
            ? """<tr><td colspan="19" class="empty">Операции не найдены</td></tr>"""
            : string.Empty;

        return $$"""
<!doctype html>
<html lang="ru">
<head>
  <meta charset="utf-8" />
  <title>Денежный журнал {{Html(fromText)}} - {{Html(toText)}}</title>
  <style>
    @page { size: A4 landscape; margin: 12mm; }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      font-family: Arial, sans-serif;
      color: #111827;
      background: #fff;
      -webkit-print-color-adjust: exact !important;
      print-color-adjust: exact !important;
    }
    .document { max-width: 1180px; margin: 0 auto; }
    header { margin-bottom: 18px; }
    .eyebrow { color: #64748b; font-size: 11px; font-weight: 700; letter-spacing: .08em; text-transform: uppercase; }
    h1 { margin: 4px 0 6px; font-size: 24px; }
    .meta { display: flex; flex-wrap: wrap; gap: 12px; color: #475569; font-size: 12px; }
    .summary { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 10px; margin: 14px 0; }
    .summary div { border: 1px solid #e2e8f0; border-radius: 8px; padding: 10px; }
    .label { color: #64748b; font-size: 11px; text-transform: uppercase; }
    .value { margin-top: 4px; font-size: 16px; font-weight: 700; }
    table { width: 100%; border-collapse: collapse; }
    th, td { border: 1px solid #cbd5e1; padding: 6px 8px; font-size: 11px; vertical-align: top; }
    th { background: #f1f5f9; color: #334155; text-align: left; }
    .num { text-align: right; white-space: nowrap; }
    .empty { padding: 24px; text-align: center; color: #64748b; }
  </style>
</head>
<body>
  <main class="document">
    <header>
      <div class="eyebrow">PayPlanner</div>
      <h1>Денежный журнал</h1>
      <div class="meta">
        <span>Период: {{Html(fromText)}} - {{Html(toText)}}</span>
        <span>Источник: {{Html(sourceText)}}</span>
        <span>Операций: {{items.Count}}</span>
      </div>
    </header>
    <section class="summary">
      <div><div class="label">Приход, факт</div><div class="value">{{Html(income.ToString("N2", ru))}}</div></div>
      <div><div class="label">Расход, факт</div><div class="value">{{Html(expense.ToString("N2", ru))}}</div></div>
      <div><div class="label">Итого</div><div class="value">{{Html((income - expense).ToString("N2", ru))}}</div></div>
    </section>
    <table>
      <thead>
        <tr>
          <th>Дата</th>
          <th>Источник</th>
          <th>Статус</th>
          <th>Движение</th>
          <th>Учет</th>
          <th>Счет</th>
          <th>Контрагент</th>
          <th>Ответственный</th>
          <th>Сумма</th>
          <th>НДС, %</th>
          <th>НДС</th>
          <th>Валюта</th>
          <th>Назначение</th>
          <th>П/п</th>
          <th>Счет</th>
          <th>Договор</th>
          <th>Акт</th>
          <th>Сверка</th>
          <th>Вложения</th>
        </tr>
      </thead>
      <tbody>
{{rows}}{{emptyRow}}
      </tbody>
    </table>
  </main>
</body>
</html>
""";
    }

    private Task<byte[]> RenderPdfAsync(string documentHtml, CancellationToken ct)
    {
        if (_pdfService is null)
        {
            throw new InvalidOperationException(
                "PDF-генератор не зарегистрирован на сервере. Проверьте настройки приложения.");
        }

        return _pdfService.RenderAsync(documentHtml, ct);
    }

    private static string Html(string value)
        => WebUtility.HtmlEncode(value);

    private static JsonSerializerOptions CreateBankStatementCategoryRulesJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static MoneyOperationSourceSummaryDto MapSourceSummaryRow(MoneyOperationSourceSummaryQueryRow row)
        => new()
        {
            Source = ParseEnum<MoneyOperationSource>(row.Source),
            StartBalance = row.StartBalance,
            Income = row.Income,
            Expense = row.Expense,
            EndBalance = row.EndBalance,
        };

    private static MoneyOperationAccountSummaryDto MapAccountSummaryRow(MoneyOperationAccountSummaryQueryRow row)
        => new()
        {
            Source = ParseEnum<MoneyOperationSource>(row.Source),
            AccountName = row.AccountName,
            StartBalance = row.StartBalance,
            Income = row.Income,
            Expense = row.Expense,
            EndBalance = row.EndBalance,
            IncomeCount = row.IncomeCount,
            ExpenseCount = row.ExpenseCount,
        };

    private static TEnum ParseEnum<TEnum>(string value)
        where TEnum : struct, Enum
        => Enum.TryParse<TEnum>(value, out var parsed) ? parsed : default;

    private async Task<string> GenerateCashOrderNumberAsync(CashOrderType type, DateTime date)
    {
        var prefix = type == CashOrderType.Incoming ? "ПКО" : "РКО";
        var yearPrefix = $"{prefix}-{date:yyyy}-";
        var count = await _db.CashOrders.CountAsync(order => order.Number.StartsWith(yearPrefix));
        return $"{yearPrefix}{count + 1:0000}";
    }

    private static string BuildCashOrderBasis(MoneyOperation operation)
    {
        var source = operation.Direction == MoneyOperationDirection.Expense
            ? "Выдача из кассы"
            : "Прием наличных в кассу";
        return string.Join(". ", new[] { source, operation.Purpose, operation.PaidInvoiceNumber }.Where(item => !string.IsNullOrWhiteSpace(item)));
    }

    private async Task<BankStatementAutoImportSettings> GetBankStatementAutoImportSettingsAsync(int userId, CancellationToken ct)
    {
        var value = await _db.UserSettings
            .AsNoTracking()
            .Where(setting => setting.UserId == userId && setting.Key == BankStatementAutoImportSettingKey)
            .Select(setting => setting.Value)
            .FirstOrDefaultAsync(ct);

        if (string.IsNullOrWhiteSpace(value))
        {
            return new BankStatementAutoImportSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<BankStatementAutoImportSettings>(value, BankStatementAutoImportJsonOptions)
                   ?? new BankStatementAutoImportSettings();
        }
        catch (JsonException)
        {
            return new BankStatementAutoImportSettings();
        }
    }

    private async Task SaveBankStatementAutoImportSettingsAsync(
        int userId,
        BankStatementAutoImportSettings settings,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var setting = await _db.UserSettings
            .FirstOrDefaultAsync(item => item.UserId == userId && item.Key == BankStatementAutoImportSettingKey, ct);
        var value = JsonSerializer.Serialize(settings, BankStatementAutoImportJsonOptions);

        if (setting is null)
        {
            _db.UserSettings.Add(new UserSetting
            {
                UserId = userId,
                Key = BankStatementAutoImportSettingKey,
                Value = value,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        else
        {
            setting.Value = value;
            setting.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(ct);
    }

    private async Task<IReadOnlyList<BankStatementCategoryRule>> GetBankStatementManualCategoryRulesAsync(
        int? userId,
        CancellationToken ct)
    {
        if (!userId.HasValue)
        {
            return [];
        }

        var value = await _db.UserSettings
            .AsNoTracking()
            .Where(setting => setting.UserId == userId.Value && setting.Key == BankStatementCategoryRulesSettingKey)
            .Select(setting => setting.Value)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        try
        {
            var settings = JsonSerializer.Deserialize<BankStatementManualCategoryRulesSettings>(
                value,
                BankStatementCategoryRulesJsonOptions);
            return settings?.Rules
                .Where(rule => rule.Enabled)
                .Select(TryCreateManualBankStatementCategoryRule)
                .Where(rule => rule is not null)
                .Cast<BankStatementCategoryRule>()
                .Take(100)
                .ToList()
                ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<string> EnumerateBankStatementAutoImportFiles(string folderPath, string fileMask)
    {
        var masks = (string.IsNullOrWhiteSpace(fileMask) ? "*.csv;*.txt;*.tsv;*.1c" : fileMask)
            .Split(new[] { ';', ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return masks
            .SelectMany(mask => Directory.EnumerateFiles(folderPath, mask, SearchOption.TopDirectoryOnly))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => System.IO.File.GetLastWriteTimeUtc(path))
            .ThenBy(Path.GetFileName)
            .ToList();
    }

    private static bool IsSupportedStatementFileName(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension is ".csv" or ".txt" or ".tsv" or ".1c";
    }

    private static void AddSkippedAutoImportFile(
        BankStatementAutoImportRunResultDto runResult,
        string fileName,
        string message)
    {
        runResult.Skipped++;
        runResult.Errors.Add($"{fileName}: {message}");
        runResult.Files.Add(new BankStatementAutoImportFileResultDto
        {
            FileName = fileName,
            Skipped = 1,
            Errors = new[] { message },
        });
    }

    private int? GetCurrentUserId()
        => int.TryParse(User?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    private async Task<InvoiceLinkValidationResult> ValidateInvoiceLinkAsync(
        MoneyOperation operation,
        int invoiceId,
        CancellationToken ct = default)
    {
        if (operation.Direction is not (MoneyOperationDirection.Income or MoneyOperationDirection.Expense))
            return new(null, "Со счётом можно сверить приходную или расходную денежную операцию.");
        var access = await ResolveMoneyInvoiceAccessAsync(operation, forWrite: true, ct);
        if (access is null) return new(null, "Счёт недоступен для сверки денежной операции.");
        var paymentType = operation.Direction == MoneyOperationDirection.Expense ? PaymentType.Expense : PaymentType.Income;
        var eligibleHeaders = await InvoicePaymentAccess.ScopeHeaders(_db, access.Section, access.CompanyId)
            .Where(payment => payment.InvoiceId == invoiceId && payment.Type == paymentType)
            .Include(payment => payment.Invoice).ToListAsync(ct);
        // Compare exact monetary values for this one invoice on both database providers.
        var invoicePayment = eligibleHeaders.OrderByDescending(payment => payment.Amount).ThenBy(payment => payment.Id).FirstOrDefault();
        if (invoicePayment?.Invoice is not { } invoice)
            return new(null, "Счёт недоступен для сверки денежной операции.");

        var invoicePayments = await _db.Payments.AsNoTracking()
            .Where(payment => payment.InvoiceId == invoiceId && payment.Status != PaymentStatus.Cancelled).ToListAsync(ct);
        var statusSourcePayments = InvoicePaymentStateService.SelectStatusSourcePayments(
            invoicePayments, invoicePayment.Id, invoicePayment.Amount);
        var outstandingAmount = statusSourcePayments.Count > 0
            ? Math.Round(statusSourcePayments.Sum(InvoicePaymentStateService.ResolveOutstandingAmount), 2, MidpointRounding.AwayFromZero)
            : CalculateOutstandingAmount(invoicePayment.Amount, invoicePayment.PaidAmount, invoicePayment.Status);
        if (outstandingAmount <= 0m)
            return new(invoice, "Счёт уже полностью оплачен, привязка денежной операции не выполнена.");
        if (operation.Amount > outstandingAmount + 0.009m && (paymentType != PaymentType.Income || !invoicePayment.ClientId.HasValue))
        {
            var message = paymentType == PaymentType.Expense
                ? $"Сумма денежной операции больше остатка по расходному счёту. Остаток: {outstandingAmount:0.00}."
                : $"Сумма денежной операции больше остатка по счёту, а клиент счёта не указан для зачисления переплаты. Остаток: {outstandingAmount:0.00}.";
            return new(invoice, message);
        }
        return new(invoice, null);
    }

    private Task ApplyMoneyOperationToInvoicePaymentAsync(
        MoneyOperation operation,
        int invoiceId,
        CancellationToken ct = default)
        => MoneyOperationInvoiceAllocationService.ApplyAsync(_db, operation, invoiceId, ct);

    private sealed class BankStatementAutoImportSettings
    {
        public bool Enabled { get; set; }
        public string? FolderPath { get; set; }
        public string FileMask { get; set; } = "*.csv;*.txt;*.1c";
        public DateTime? LastCheckedAt { get; set; }
    }

    private sealed record InvoiceLinkValidationResult(Invoice? Invoice, string? Error);

    public sealed class LinkMoneyOperationInvoiceRequest
    {
        public int InvoiceId { get; init; }
        public string? Comment { get; init; }
    }



    private sealed class MoneyOperationSourceSummaryQueryRow
    {
        public string Source { get; init; } = string.Empty;
        public decimal StartBalance { get; init; }
        public decimal Income { get; init; }
        public decimal Expense { get; init; }
        public decimal EndBalance { get; init; }
    }

    private sealed class MoneyOperationAccountSummaryQueryRow
    {
        public string Source { get; init; } = string.Empty;
        public string AccountName { get; init; } = string.Empty;
        public decimal StartBalance { get; init; }
        public decimal Income { get; init; }
        public decimal Expense { get; init; }
        public decimal EndBalance { get; init; }
        public int IncomeCount { get; init; }
        public int ExpenseCount { get; init; }
    }






}
