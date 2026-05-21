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
public class MoneyOperationsController : ControllerBase
{
    private const long MaxStatementFileSizeBytes = 10L * 1024L * 1024L;
    private const int MaxAutoImportStatementFiles = 20;
    private const string BankStatementAutoImportSettingKey = "money.bankStatement.autoImport";
    private const string BankStatementCategoryRulesSettingKey = "money.bankStatement.categoryRules";
    private const string CashSourceToken = "\u043A\u0430\u0441";
    private const string CashSourceTitleToken = "\u041A\u0430\u0441";
    private const string CashSourceAlternativeToken = "\u043D\u0430\u043B\u0438\u0447";
    private const string CashSourceAlternativeTitleToken = "\u041D\u0430\u043B\u0438\u0447";
    private const string CardSourceToken = "\u043A\u0430\u0440\u0442";
    private const string CardSourceTitleToken = "\u041A\u0430\u0440\u0442";
    private const string EMoneySourceToken = "e-money";
    private const string EMoneySourceTitleToken = "E-money";
    private const string EMoneySourceCompactToken = "emoney";
    private const string EMoneySourceCompactTitleToken = "EMoney";
    private const string EMoneySourceRussianToken = "\u044D\u043B\u0435\u043A\u0442\u0440\u043E\u043D";
    private const string EMoneySourceRussianTitleToken = "\u042D\u043B\u0435\u043A\u0442\u0440\u043E\u043D";
    private const string EMoneySourceYuMoneyToken = "\u042EMoney";
    private const string EMoneySourceYooMoneyToken = "YooMoney";
    private const string EMoneySourceYandexToken = "\u042F\u043D\u0434\u0435\u043A\u0441";
    private static readonly BankStatementCategoryRule[] BankStatementCategoryRules =
    [
        new("Оплата от клиентов", PaymentType.Income,
        [
            "оплата по счету",
            "оплата услуг",
            "за услуги",
            "по договору оказания",
            "поступление от клиента",
            "оплата от покупателя",
        ]),
        new("Возврат от поставщика", PaymentType.Income,
        [
            "возврат от поставщика",
            "возврат оплаты",
            "возврат денежных средств",
            "возврат переплаты",
        ]),
        new("Эквайринг", PaymentType.Income,
        [
            "эквайринг",
            "поступление по эквайрингу",
            "зачисление по терминалу",
        ]),
        new("Банковская комиссия", PaymentType.Expense,
        [
            "банковская комиссия",
            "комиссия банка",
            "комиссия за",
            "расчетно кассов",
            "рко",
            "обслуживание счета",
            "эквайринг",
        ]),
        new("Налоги и сборы", PaymentType.Expense,
        [
            "налог",
            "ндс",
            "ндфл",
            "усн",
            "страховые взносы",
            "взносы",
            "пени",
            "штраф",
            "казначейство",
            "фнс",
        ]),
        new("Зарплата", PaymentType.Expense,
        [
            "заработная плата",
            "зарплата",
            "аванс",
            "реестр на выплату",
            "по ведомости",
        ]),
        new("Аренда", PaymentType.Expense,
        [
            "арендная плата",
            "аренда",
            "аренда офиса",
            "за помещение",
        ]),
        new("Расчеты с поставщиками", PaymentType.Expense,
        [
            "оплата поставщику",
            "за материалы",
            "за товары",
            "по договору поставки",
            "за оборудование",
            "за услуги связи",
        ]),
        new("Возврат клиенту", PaymentType.Expense,
        [
            "возврат клиенту",
            "возврат покупателю",
            "возврат оплаты",
            "возврат денежных средств",
        ]),
    ];

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

    private Task<DataVisibilityAccess?> ResolveMoneyVisibilityAsync(CancellationToken ct)
        => _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Payments, ct);

    private async Task<bool> CanExportMoneyOperationsAsync(CancellationToken ct)
    {
        var userId = GetCurrentUserId();
        if (!userId.HasValue)
        {
            return User?.IsInRole("admin") == true || User?.Identity?.IsAuthenticated != true;
        }

        if (User.IsInRole("admin") || await _db.IsUserAdminAsync(userId.Value, ct))
        {
            return true;
        }

        var permission = await _db.GetUserSectionPermissionAsync(userId.Value, RolePermissionCatalog.Payments, ct);
        return permission?.CanExport == true;
    }

    private static IQueryable<MoneyOperationQueryRow> ApplyMoneyOperationVisibility(
        IQueryable<MoneyOperationQueryRow> query,
        DataVisibilityAccess? visibility)
    {
        if (visibility is null)
        {
            return query.Where(_ => false);
        }

        if (visibility.SeesAll)
        {
            return query;
        }

        var visibleUserIds = visibility.VisibleUserIds.ToArray();
        return query.Where(item =>
            item.ResponsibleUserId.HasValue
            && visibleUserIds.Contains(item.ResponsibleUserId.Value));
    }

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

        var balances = await SqliteRetryHelper.ExecuteAsync(
            cancellationToken => BuildDailyBalanceRowsAsync(from, to, source, accountName, visibility, cancellationToken),
            _logger,
            ct);

        return Ok(balances);
    }

    [HttpGet("balance-reconciliation")]
    public async Task<ActionResult<IReadOnlyList<MoneyBalanceReconciliationResultDto>>> GetBalanceReconciliationResults(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] MoneyOperationSource? source,
        [FromQuery] string? accountName,
        CancellationToken ct = default)
    {
        var results = await SqliteRetryHelper.ExecuteAsync(async cancellationToken =>
        {
            var query = _db.MoneyBalanceReconciliationResults.AsNoTracking().AsQueryable();
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
        var visibility = await ResolveMoneyVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var result = await SqliteRetryHelper.ExecuteAsync(async cancellationToken =>
        {
            var balances = await BuildDailyBalanceRowsAsync(from, to, source, accountName, visibility, cancellationToken);
            var keys = balances
                .Select(item => new { item.Source, item.AccountName, Date = item.Date.Date })
                .ToList();
            var keyDates = keys.Select(key => key.Date).Distinct().ToList();
            var existingRows = keys.Count == 0
                ? new List<MoneyBalanceReconciliationResult>()
                : await _db.MoneyBalanceReconciliationResults
                    .Where(item => keyDates.Contains(item.Date))
                    .ToListAsync(cancellationToken);
            var existingByKey = existingRows
                .Where(item => keys.Any(key =>
                    key.Source == item.Source &&
                    key.AccountName == item.AccountName &&
                    key.Date == item.Date.Date))
                .ToDictionary(item => (item.Source, item.AccountName, item.Date.Date));
            var checkedAt = DateTime.UtcNow;
            var checkedByUserId = GetCurrentUserId();
            var created = 0;
            var updated = 0;

            foreach (var balance in balances)
            {
                var key = (balance.Source, balance.AccountName, balance.Date.Date);
                if (!existingByKey.TryGetValue(key, out var row))
                {
                    row = new MoneyBalanceReconciliationResult
                    {
                        Date = balance.Date.Date,
                        Source = balance.Source,
                        AccountName = balance.AccountName,
                    };
                    _db.MoneyBalanceReconciliationResults.Add(row);
                    created++;
                }
                else
                {
                    updated++;
                }

                row.OpeningBalance = balance.OpeningBalance;
                row.Income = balance.Income;
                row.Expense = balance.Expense;
                row.ClosingBalance = balance.ClosingBalance;
                row.IncomeCount = balance.IncomeCount;
                row.ExpenseCount = balance.ExpenseCount;
                row.IsBalanced = balance.IsBalanced;
                row.IssueCount = balance.IsBalanced ? 0 : 1;
                row.CheckedAt = checkedAt;
                row.CheckedByUserId = checkedByUserId;
            }

            await _db.SaveChangesAsync(cancellationToken);
            var storedItems = balances.Count == 0
                ? new List<MoneyBalanceReconciliationResult>()
                : await _db.MoneyBalanceReconciliationResults
                    .AsNoTracking()
                    .Where(item => keyDates.Contains(item.Date))
                    .ToListAsync(cancellationToken);
            var itemDtos = storedItems
                .Where(item => keys.Any(key =>
                    key.Source == item.Source &&
                    key.AccountName == item.AccountName &&
                    key.Date == item.Date.Date))
                .OrderBy(item => item.Date)
                .ThenBy(item => item.AccountName)
                .ThenBy(item => item.Source)
                .Select(MapBalanceReconciliationResult)
                .ToList();

            return new MoneyBalanceReconciliationRunResultDto
            {
                CheckedAt = checkedAt,
                Created = created,
                Updated = updated,
                IssueCount = itemDtos.Sum(item => item.IssueCount),
                Items = itemDtos,
            };
        }, _logger, ct);

        return Ok(result);
    }

    private async Task<List<MoneyOperationDailyBalanceDto>> BuildDailyBalanceRowsAsync(
        DateTime? from,
        DateTime? to,
        MoneyOperationSource? source,
        string? accountName,
        DataVisibilityAccess visibility,
        CancellationToken cancellationToken)
    {
        var openingQuery = from.HasValue
            ? ApplyMoneyOperationVisibility(
                BuildMoneyOperationsQuery(null, from.Value.Date.AddDays(-1), source, null, MoneyOperationAccountingType.Fact, null, accountName, null, null),
                visibility)
                .Where(item => item.AccountName != null && item.AccountName != "")
            : null;

        var openingBalances = new Dictionary<(MoneyOperationSource Source, string AccountName), decimal>();
        if (openingQuery is not null)
        {
            var openingRows = await openingQuery
                .GroupBy(item => new { item.Source, item.AccountName })
                .Select(group => new MoneyOperationDailyBalanceOpeningQueryRow
                {
                    Source = group.Key.Source,
                    AccountName = group.Key.AccountName!,
                    Balance =
                        group.Where(item => item.Direction == MoneyOperationDirection.Income.ToString()).Sum(item => item.Amount) -
                        group.Where(item => item.Direction == MoneyOperationDirection.Expense.ToString()).Sum(item => item.Amount),
                })
                .ToListAsync(cancellationToken);

            openingBalances = openingRows
                .Select(row => new
                {
                    Source = ParseEnum<MoneyOperationSource>(row.Source),
                    row.AccountName,
                    row.Balance,
                })
                .ToDictionary(row => (row.Source, row.AccountName), row => row.Balance);
        }

        var movementRows = await ApplyMoneyOperationVisibility(
                BuildMoneyOperationsQuery(from, to, source, null, MoneyOperationAccountingType.Fact, null, accountName, null, null),
                visibility)
            .Where(item => item.AccountName != null && item.AccountName != "")
            .GroupBy(item => new { item.Source, item.AccountName, Date = item.Date.Date })
            .Select(group => new MoneyOperationDailyBalanceQueryRow
            {
                Date = group.Key.Date,
                Source = group.Key.Source,
                AccountName = group.Key.AccountName!,
                Income = group
                    .Where(item => item.Direction == MoneyOperationDirection.Income.ToString())
                    .Sum(item => item.Amount),
                Expense = group
                    .Where(item => item.Direction == MoneyOperationDirection.Expense.ToString())
                    .Sum(item => item.Amount),
                IncomeCount = group.Count(item => item.Direction == MoneyOperationDirection.Income.ToString()),
                ExpenseCount = group.Count(item => item.Direction == MoneyOperationDirection.Expense.ToString()),
            })
            .ToListAsync(cancellationToken);

        return movementRows
            .Select(row => new
            {
                Row = row,
                Source = ParseEnum<MoneyOperationSource>(row.Source),
            })
            .GroupBy(item => new { item.Source, item.Row.AccountName })
            .OrderBy(group => group.Key.AccountName)
            .ThenBy(group => group.Key.Source)
            .SelectMany(group =>
            {
                var runningBalance = openingBalances.TryGetValue((group.Key.Source, group.Key.AccountName), out var openingBalance)
                    ? openingBalance
                    : 0m;

                return group
                    .OrderBy(item => item.Row.Date)
                    .Select(item =>
                    {
                        var row = item.Row;
                        var closingBalance = runningBalance + row.Income - row.Expense;
                        var result = new MoneyOperationDailyBalanceDto
                        {
                            Date = row.Date,
                            Source = item.Source,
                            AccountName = row.AccountName,
                            OpeningBalance = runningBalance,
                            Income = row.Income,
                            Expense = row.Expense,
                            ClosingBalance = closingBalance,
                            IncomeCount = row.IncomeCount,
                            ExpenseCount = row.ExpenseCount,
                            IsBalanced = closingBalance == runningBalance + row.Income - row.Expense,
                        };
                        runningBalance = closingBalance;
                        return result;
                    });
            })
            .OrderBy(row => row.Date)
            .ThenBy(row => row.AccountName)
            .ThenBy(row => row.Source)
            .ToList();
    }

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
    {
        var customOperations = ApplyMoneyOperationFilters(
                _db.MoneyOperations.AsNoTracking(),
                from,
                to,
                source,
                direction,
                accountingType,
                moneyAccountId,
                accountName,
                categoryName,
                search)
            .Select(operation => new MoneyOperationQueryRow
            {
                Id = operation.Id,
                Source = operation.Source == MoneyOperationSource.Cash
                    ? nameof(MoneyOperationSource.Cash)
                    : operation.Source == MoneyOperationSource.Card
                        ? nameof(MoneyOperationSource.Card)
                        : operation.Source == MoneyOperationSource.EMoney
                            ? nameof(MoneyOperationSource.EMoney)
                            : nameof(MoneyOperationSource.Bank),
                Direction = operation.Direction == MoneyOperationDirection.Income
                    ? nameof(MoneyOperationDirection.Income)
                    : operation.Direction == MoneyOperationDirection.Expense
                        ? nameof(MoneyOperationDirection.Expense)
                        : nameof(MoneyOperationDirection.Transfer),
                AccountingType = operation.AccountingType == MoneyOperationAccountingType.Fact
                    ? nameof(MoneyOperationAccountingType.Fact)
                    : nameof(MoneyOperationAccountingType.Plan),
                MoneyAccountId = operation.MoneyAccountId,
                MoneyAccountName = operation.MoneyAccount != null ? operation.MoneyAccount.Name : null,
                AccountName = operation.AccountName,
                CounterpartyName = operation.CounterpartyName,
                ResponsibleUserId = operation.ResponsibleUserId ?? _db.Payments
                    .Where(payment => operation.LinkedInvoiceId.HasValue && payment.InvoiceId == operation.LinkedInvoiceId.Value)
                    .SelectMany(payment => payment.Responsibles)
                    .OrderBy(link => link.UserId)
                    .Select(link => (int?)link.UserId)
                    .FirstOrDefault(),
                ResponsibleName = operation.ResponsibleUser != null
                    ? (operation.ResponsibleUser.FullName != null && operation.ResponsibleUser.FullName != ""
                        ? operation.ResponsibleUser.FullName
                        : operation.ResponsibleUser.Email)
                    : _db.Payments
                    .Where(payment => operation.LinkedInvoiceId.HasValue && payment.InvoiceId == operation.LinkedInvoiceId.Value)
                    .SelectMany(payment => payment.Responsibles)
                    .OrderBy(link => link.UserId)
                    .Select(link => link.User.FullName != null && link.User.FullName != "" ? link.User.FullName : link.User.Email)
                    .FirstOrDefault(),
                IncomeTypeId = operation.IncomeTypeId,
                CategoryName = operation.IncomeType != null ? operation.IncomeType.Name : operation.CategoryName,
                Amount = operation.Amount,
                VatRate = operation.VatRate,
                VatAmount = operation.VatAmount,
                Currency = operation.Currency,
                PaymentOrderNumber = operation.PaymentOrderNumber,
                Date = operation.Date,
                RecognitionDate = operation.RecognitionDate,
                IsAdvancePayment = operation.IsAdvancePayment,
                Purpose = operation.Purpose,
                PaidInvoiceNumber = operation.PaidInvoiceNumber,
                PaidAmount = operation.PaidAmount,
                ProjectName = operation.ProjectName,
                DirectionName = operation.DirectionName,
                SalesPointName = operation.SalesPointName,
                PaymentPriority = operation.PaymentPriority,
                PaidAccountName = operation.PaidAccountName,
                LinkedInvoiceId = operation.LinkedInvoiceId,
                LinkedInvoiceNumber = operation.LinkedInvoice != null ? operation.LinkedInvoice.Number : null,
                LinkedInvoiceDate = operation.LinkedInvoice != null ? operation.LinkedInvoice.Date : null,
                LinkedContractId = _db.Payments
                    .Where(payment => operation.LinkedInvoiceId.HasValue && payment.InvoiceId == operation.LinkedInvoiceId.Value)
                    .OrderBy(payment => payment.Id)
                    .Select(payment => payment.ContractId)
                    .FirstOrDefault(),
                LinkedContractNumber = _db.Payments
                    .Where(payment => operation.LinkedInvoiceId.HasValue && payment.InvoiceId == operation.LinkedInvoiceId.Value)
                    .OrderBy(payment => payment.Id)
                    .Select(payment => payment.Contract != null ? payment.Contract.Number : null)
                    .FirstOrDefault(),
                LinkedActId = _db.Acts
                    .Where(act =>
                        act.InvoiceId.HasValue &&
                        act.Invoice != null &&
                        operation.LinkedInvoiceId.HasValue &&
                        act.Invoice.InvoiceId == operation.LinkedInvoiceId.Value &&
                        act.Status != ActStatus.Terminated)
                    .OrderByDescending(act => act.Status == ActStatus.Signed)
                    .ThenByDescending(act => act.Date)
                    .ThenByDescending(act => act.Id)
                    .Select(act => (int?)act.Id)
                    .FirstOrDefault(),
                LinkedActNumber = _db.Acts
                    .Where(act =>
                        act.InvoiceId.HasValue &&
                        act.Invoice != null &&
                        operation.LinkedInvoiceId.HasValue &&
                        act.Invoice.InvoiceId == operation.LinkedInvoiceId.Value &&
                        act.Status != ActStatus.Terminated)
                    .OrderByDescending(act => act.Status == ActStatus.Signed)
                    .ThenByDescending(act => act.Date)
                    .ThenByDescending(act => act.Id)
                    .Select(act => act.Number)
                    .FirstOrDefault(),
                LinkedActDate = _db.Acts
                    .Where(act =>
                        act.InvoiceId.HasValue &&
                        act.Invoice != null &&
                        operation.LinkedInvoiceId.HasValue &&
                        act.Invoice.InvoiceId == operation.LinkedInvoiceId.Value &&
                        act.Status != ActStatus.Terminated)
                    .OrderByDescending(act => act.Status == ActStatus.Signed)
                    .ThenByDescending(act => act.Date)
                    .ThenByDescending(act => act.Id)
                    .Select(act => (DateTime?)act.Date)
                    .FirstOrDefault(),
                MatchStatus = operation.MatchStatus == MoneyOperationMatchStatus.Matched
                    ? nameof(MoneyOperationMatchStatus.Matched)
                    : operation.MatchStatus == MoneyOperationMatchStatus.ClarificationRequired
                        ? nameof(MoneyOperationMatchStatus.ClarificationRequired)
                        : operation.MatchStatus == MoneyOperationMatchStatus.Ignored
                            ? nameof(MoneyOperationMatchStatus.Ignored)
                            : nameof(MoneyOperationMatchStatus.None),
                MatchedAt = operation.MatchedAt,
                MatchComment = operation.MatchComment,
                AttachmentCount = operation.BankStatementImportBatchId.HasValue ? 1 : null,
                AttachmentSummary = operation.BankStatementImportBatch != null ? operation.BankStatementImportBatch.FileName : null,
                CreatedAt = operation.CreatedAt,
                UpdatedAt = operation.UpdatedAt,
                IsReadonly = false,
                RelatedPaymentId = null,
                CashOrderId = _db.CashOrders
                    .Where(order => order.MoneyOperationId == operation.Id && order.Status != CashOrderStatus.Cancelled)
                    .OrderByDescending(order => order.Id)
                    .Select(order => (int?)order.Id)
                    .FirstOrDefault(),
                CashOrderNumber = _db.CashOrders
                    .Where(order => order.MoneyOperationId == operation.Id && order.Status != CashOrderStatus.Cancelled)
                    .OrderByDescending(order => order.Id)
                    .Select(order => order.Number)
                    .FirstOrDefault(),
                CashOrderDate = _db.CashOrders
                    .Where(order => order.MoneyOperationId == operation.Id && order.Status != CashOrderStatus.Cancelled)
                    .OrderByDescending(order => order.Id)
                    .Select(order => (DateTime?)order.Date)
                    .FirstOrDefault(),
                CashOrderStatus = _db.CashOrders
                    .Where(order => order.MoneyOperationId == operation.Id && order.Status != CashOrderStatus.Cancelled)
                    .OrderByDescending(order => order.Id)
                    .Select(order => order.Status == CashOrderStatus.Posted
                        ? nameof(CashOrderStatus.Posted)
                        : order.Status == CashOrderStatus.Draft
                            ? nameof(CashOrderStatus.Draft)
                            : nameof(CashOrderStatus.Cancelled))
                    .FirstOrDefault(),
                CanCreateCashOrder =
                    operation.Source == MoneyOperationSource.Cash &&
                    operation.AccountingType == MoneyOperationAccountingType.Fact &&
                    operation.Amount > 0m &&
                    (operation.Direction == MoneyOperationDirection.Income ||
                     operation.Direction == MoneyOperationDirection.Expense) &&
                    !_db.CashOrders.Any(order => order.MoneyOperationId == operation.Id && order.Status != CashOrderStatus.Cancelled),
            });

        var paymentOperations = ApplyPaymentFilters(
                _db.Payments.AsNoTracking(),
                from,
                to,
                source,
                direction,
                accountingType,
                moneyAccountId,
                accountName,
                categoryName,
                search)
            .Select(payment => new MoneyOperationQueryRow
            {
                Id = -payment.Id,
                Source =
                    payment.PaymentSource != null &&
                    payment.PaymentSource.Name != null &&
                    (payment.PaymentSource.Name.Contains(CashSourceToken) ||
                     payment.PaymentSource.Name.Contains(CashSourceTitleToken) ||
                     payment.PaymentSource.Name.Contains(CashSourceAlternativeToken) ||
                     payment.PaymentSource.Name.Contains(CashSourceAlternativeTitleToken))
                        ? nameof(MoneyOperationSource.Cash)
                        : payment.PaymentSource != null &&
                          payment.PaymentSource.Name != null &&
                          (payment.PaymentSource.Name.Contains(CardSourceToken) ||
                           payment.PaymentSource.Name.Contains(CardSourceTitleToken))
                            ? nameof(MoneyOperationSource.Card)
                            : payment.PaymentSource != null &&
                              payment.PaymentSource.Name != null &&
                              (payment.PaymentSource.Name.Contains(EMoneySourceToken) ||
                               payment.PaymentSource.Name.Contains(EMoneySourceTitleToken) ||
                               payment.PaymentSource.Name.Contains(EMoneySourceCompactToken) ||
                               payment.PaymentSource.Name.Contains(EMoneySourceCompactTitleToken) ||
                               payment.PaymentSource.Name.Contains(EMoneySourceRussianToken) ||
                               payment.PaymentSource.Name.Contains(EMoneySourceRussianTitleToken) ||
                               payment.PaymentSource.Name.Contains(EMoneySourceYuMoneyToken) ||
                               payment.PaymentSource.Name.Contains(EMoneySourceYooMoneyToken) ||
                               payment.PaymentSource.Name.Contains(EMoneySourceYandexToken))
                                ? nameof(MoneyOperationSource.EMoney)
                                : nameof(MoneyOperationSource.Bank),
                Direction = payment.Type == PaymentType.Income
                    ? nameof(MoneyOperationDirection.Income)
                    : nameof(MoneyOperationDirection.Expense),
                AccountingType = payment.Status == PaymentStatus.Completed
                    ? nameof(MoneyOperationAccountingType.Fact)
                    : nameof(MoneyOperationAccountingType.Plan),
                MoneyAccountId = null,
                MoneyAccountName = null,
                AccountName = payment.Account,
                CounterpartyName = payment.CounterpartyName,
                ResponsibleUserId = payment.Responsibles
                    .OrderBy(link => link.UserId)
                    .Select(link => (int?)link.UserId)
                    .FirstOrDefault(),
                ResponsibleName = payment.Responsibles
                    .OrderBy(link => link.UserId)
                    .Select(link => link.User.FullName != null && link.User.FullName != "" ? link.User.FullName : link.User.Email)
                    .FirstOrDefault(),
                IncomeTypeId = payment.IncomeTypeId,
                CategoryName = payment.IncomeType != null
                    ? payment.IncomeType.Name
                    : payment.ClientCase != null
                        ? payment.ClientCase.Title
                        : null,
                Amount =
                    payment.Status == PaymentStatus.Completed && payment.PaidAmount > 0m
                        ? payment.PaidAmount
                        : payment.Amount,
                VatRate = null,
                VatAmount = null,
                Currency = "RUB",
                PaymentOrderNumber = null,
                Date = payment.Status == PaymentStatus.Completed
                    ? (payment.LastPaymentDate ?? payment.PaidDate ?? payment.Date)
                    : payment.Date,
                RecognitionDate = payment.Date,
                IsAdvancePayment = false,
                Purpose = payment.Description,
                PaidInvoiceNumber = payment.Account,
                PaidAmount = payment.PaidAmount,
                ProjectName = payment.ClientCase != null ? payment.ClientCase.Title : null,
                DirectionName = payment.PaymentSource != null ? payment.PaymentSource.Name : null,
                SalesPointName = null,
                PaymentPriority = null,
                PaidAccountName = payment.Account,
                LinkedInvoiceId = payment.InvoiceId,
                LinkedInvoiceNumber = payment.Invoice != null ? payment.Invoice.Number : payment.Account,
                LinkedInvoiceDate = payment.Invoice != null ? payment.Invoice.Date : payment.AccountDate,
                LinkedContractId = payment.ContractId,
                LinkedContractNumber = payment.Contract != null ? payment.Contract.Number : null,
                LinkedActId = _db.Acts
                    .Where(act => act.InvoiceId == payment.Id && act.Status != ActStatus.Terminated)
                    .OrderByDescending(act => act.Status == ActStatus.Signed)
                    .ThenByDescending(act => act.Date)
                    .ThenByDescending(act => act.Id)
                    .Select(act => (int?)act.Id)
                    .FirstOrDefault(),
                LinkedActNumber = _db.Acts
                    .Where(act => act.InvoiceId == payment.Id && act.Status != ActStatus.Terminated)
                    .OrderByDescending(act => act.Status == ActStatus.Signed)
                    .ThenByDescending(act => act.Date)
                    .ThenByDescending(act => act.Id)
                    .Select(act => act.Number)
                    .FirstOrDefault(),
                LinkedActDate = _db.Acts
                    .Where(act => act.InvoiceId == payment.Id && act.Status != ActStatus.Terminated)
                    .OrderByDescending(act => act.Status == ActStatus.Signed)
                    .ThenByDescending(act => act.Date)
                    .ThenByDescending(act => act.Id)
                    .Select(act => (DateTime?)act.Date)
                    .FirstOrDefault(),
                MatchStatus = payment.InvoiceId.HasValue
                    ? nameof(MoneyOperationMatchStatus.Matched)
                    : nameof(MoneyOperationMatchStatus.None),
                MatchedAt = payment.LastPaymentDate ?? payment.PaidDate,
                MatchComment = null,
                AttachmentCount = null,
                AttachmentSummary = null,
                CreatedAt = payment.CreatedAt,
                UpdatedAt = null,
                IsReadonly = true,
                RelatedPaymentId = payment.Id,
                CashOrderId = _db.CashOrders
                    .Where(order => order.PaymentId == payment.Id && order.Status != CashOrderStatus.Cancelled)
                    .OrderByDescending(order => order.Id)
                    .Select(order => (int?)order.Id)
                    .FirstOrDefault(),
                CashOrderNumber = _db.CashOrders
                    .Where(order => order.PaymentId == payment.Id && order.Status != CashOrderStatus.Cancelled)
                    .OrderByDescending(order => order.Id)
                    .Select(order => order.Number)
                    .FirstOrDefault(),
                CashOrderDate = _db.CashOrders
                    .Where(order => order.PaymentId == payment.Id && order.Status != CashOrderStatus.Cancelled)
                    .OrderByDescending(order => order.Id)
                    .Select(order => (DateTime?)order.Date)
                    .FirstOrDefault(),
                CashOrderStatus = _db.CashOrders
                    .Where(order => order.PaymentId == payment.Id && order.Status != CashOrderStatus.Cancelled)
                    .OrderByDescending(order => order.Id)
                    .Select(order => order.Status == CashOrderStatus.Posted
                        ? nameof(CashOrderStatus.Posted)
                        : order.Status == CashOrderStatus.Draft
                            ? nameof(CashOrderStatus.Draft)
                            : nameof(CashOrderStatus.Cancelled))
                    .FirstOrDefault(),
                CanCreateCashOrder =
                    payment.PaymentSource != null &&
                    payment.PaymentSource.Name != null &&
                    (payment.PaymentSource.Name.Contains(CashSourceToken) ||
                     payment.PaymentSource.Name.Contains(CashSourceTitleToken) ||
                     payment.PaymentSource.Name.Contains(CashSourceAlternativeToken) ||
                     payment.PaymentSource.Name.Contains(CashSourceAlternativeTitleToken)) &&
                    !_db.CashOrders.Any(order => order.PaymentId == payment.Id && order.Status != CashOrderStatus.Cancelled),
            });

        var combinedQuery = customOperations.Concat(paymentOperations);

        return combinedQuery;
    }

    private static IQueryable<MoneyOperation> ApplyMoneyOperationFilters(
        IQueryable<MoneyOperation> query,
        DateTime? from,
        DateTime? to,
        MoneyOperationSource? source,
        MoneyOperationDirection? direction,
        MoneyOperationAccountingType? accountingType,
        int? moneyAccountId,
        string? accountName,
        string? categoryName,
        string? search)
    {
        if (from.HasValue)
        {
            var fromDate = from.Value.Date;
            query = query.Where(operation => operation.Date >= fromDate);
        }

        if (to.HasValue)
        {
            var toDate = to.Value.Date;
            query = query.Where(operation => operation.Date <= toDate);
        }

        if (source.HasValue)
        {
            query = query.Where(operation => operation.Source == source.Value);
        }

        if (direction.HasValue)
        {
            query = query.Where(operation => operation.Direction == direction.Value);
        }

        if (accountingType.HasValue)
        {
            query = query.Where(operation => operation.AccountingType == accountingType.Value);
        }

        if (moneyAccountId.HasValue)
        {
            query = query.Where(operation => operation.MoneyAccountId == moneyAccountId.Value);
        }

        if (!string.IsNullOrWhiteSpace(accountName))
        {
            var accountTerm = accountName.Trim();
            query = query.Where(operation => operation.AccountName == accountTerm);
        }

        if (!string.IsNullOrWhiteSpace(categoryName))
        {
            var categoryTerm = categoryName.Trim();
            var categoryTitleTerm = ToTitleSearchTerm(categoryTerm);
            query = query.Where(operation =>
                (operation.CategoryName ?? string.Empty).Contains(categoryTerm) ||
                (operation.IncomeType != null && operation.IncomeType.Name.Contains(categoryTerm)) ||
                (categoryTitleTerm != categoryTerm &&
                    ((operation.CategoryName ?? string.Empty).Contains(categoryTitleTerm) ||
                     (operation.IncomeType != null && operation.IncomeType.Name.Contains(categoryTitleTerm)))));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var titleTerm = ToTitleSearchTerm(term);
            query = query.Where(operation =>
                (operation.AccountName ?? string.Empty).Contains(term) ||
                (operation.CounterpartyName ?? string.Empty).Contains(term) ||
                (operation.Purpose ?? string.Empty).Contains(term) ||
                (operation.PaidInvoiceNumber ?? string.Empty).Contains(term) ||
                (operation.CategoryName ?? string.Empty).Contains(term) ||
                (operation.IncomeType != null && operation.IncomeType.Name.Contains(term)) ||
                (operation.ResponsibleUser != null &&
                    ((operation.ResponsibleUser.FullName ?? string.Empty).Contains(term) ||
                     operation.ResponsibleUser.Email.Contains(term))) ||
                (operation.LinkedInvoice != null && operation.LinkedInvoice.Number.Contains(term)) ||
                (operation.BankStatementImportBatch != null && operation.BankStatementImportBatch.FileName.Contains(term)) ||
                (operation.LinkedInvoice != null && operation.LinkedInvoice.Payments.Any(payment =>
                    (payment.Contract != null &&
                        ((payment.Contract.Number ?? string.Empty).Contains(term) ||
                         (payment.Contract.Title ?? string.Empty).Contains(term))) ||
                    payment.Responsibles.Any(link =>
                        (link.User.FullName ?? string.Empty).Contains(term) ||
                        link.User.Email.Contains(term)))) ||
                (titleTerm != term &&
                    ((operation.AccountName ?? string.Empty).Contains(titleTerm) ||
                     (operation.CounterpartyName ?? string.Empty).Contains(titleTerm) ||
                     (operation.Purpose ?? string.Empty).Contains(titleTerm) ||
                     (operation.PaidInvoiceNumber ?? string.Empty).Contains(titleTerm) ||
                      (operation.CategoryName ?? string.Empty).Contains(titleTerm) ||
                      (operation.IncomeType != null && operation.IncomeType.Name.Contains(titleTerm)) ||
                      (operation.ResponsibleUser != null &&
                          ((operation.ResponsibleUser.FullName ?? string.Empty).Contains(titleTerm) ||
                           operation.ResponsibleUser.Email.Contains(titleTerm))) ||
                      (operation.LinkedInvoice != null && operation.LinkedInvoice.Number.Contains(titleTerm)) ||
                     (operation.BankStatementImportBatch != null && operation.BankStatementImportBatch.FileName.Contains(titleTerm)) ||
                     (operation.LinkedInvoice != null && operation.LinkedInvoice.Payments.Any(payment =>
                         (payment.Contract != null &&
                             ((payment.Contract.Number ?? string.Empty).Contains(titleTerm) ||
                              (payment.Contract.Title ?? string.Empty).Contains(titleTerm))) ||
                         payment.Responsibles.Any(link =>
                             (link.User.FullName ?? string.Empty).Contains(titleTerm) ||
                             link.User.Email.Contains(titleTerm)))))));
        }

        return query;
    }

    private static IQueryable<Payment> ApplyPaymentFilters(
        IQueryable<Payment> query,
        DateTime? from,
        DateTime? to,
        MoneyOperationSource? source,
        MoneyOperationDirection? direction,
        MoneyOperationAccountingType? accountingType,
        int? moneyAccountId,
        string? accountName,
        string? categoryName,
        string? search)
    {
        query = query.Where(payment =>
            payment.Status == PaymentStatus.Completed &&
            payment.DocumentKind != DocumentKind.Invoice &&
            payment.InvoiceId == null);

        if (moneyAccountId.HasValue)
        {
            query = query.Where(_ => false);
        }

        if (from.HasValue)
        {
            var fromDate = from.Value.Date;
            query = query.Where(payment => (payment.LastPaymentDate ?? payment.PaidDate ?? payment.Date) >= fromDate);
        }

        if (to.HasValue)
        {
            var toDate = to.Value.Date;
            query = query.Where(payment => (payment.LastPaymentDate ?? payment.PaidDate ?? payment.Date) <= toDate);
        }

        if (source.HasValue)
        {
            query = source.Value switch
            {
                MoneyOperationSource.Cash => query.Where(payment =>
                    payment.PaymentSource != null &&
                    payment.PaymentSource.Name != null &&
                    (payment.PaymentSource.Name.Contains(CashSourceToken) ||
                     payment.PaymentSource.Name.Contains(CashSourceTitleToken) ||
                     payment.PaymentSource.Name.Contains(CashSourceAlternativeToken) ||
                     payment.PaymentSource.Name.Contains(CashSourceAlternativeTitleToken))),
                MoneyOperationSource.Card => query.Where(payment =>
                    payment.PaymentSource != null &&
                    payment.PaymentSource.Name != null &&
                    (payment.PaymentSource.Name.Contains(CardSourceToken) ||
                     payment.PaymentSource.Name.Contains(CardSourceTitleToken))),
                MoneyOperationSource.EMoney => query.Where(payment =>
                    payment.PaymentSource != null &&
                    payment.PaymentSource.Name != null &&
                    (payment.PaymentSource.Name.Contains(EMoneySourceToken) ||
                     payment.PaymentSource.Name.Contains(EMoneySourceTitleToken) ||
                     payment.PaymentSource.Name.Contains(EMoneySourceCompactToken) ||
                     payment.PaymentSource.Name.Contains(EMoneySourceCompactTitleToken) ||
                     payment.PaymentSource.Name.Contains(EMoneySourceRussianToken) ||
                     payment.PaymentSource.Name.Contains(EMoneySourceRussianTitleToken) ||
                     payment.PaymentSource.Name.Contains(EMoneySourceYuMoneyToken) ||
                     payment.PaymentSource.Name.Contains(EMoneySourceYooMoneyToken) ||
                     payment.PaymentSource.Name.Contains(EMoneySourceYandexToken))),
                MoneyOperationSource.Bank => query.Where(payment =>
                    payment.PaymentSource == null ||
                    payment.PaymentSource.Name == null ||
                    (!payment.PaymentSource.Name.Contains(CashSourceToken) &&
                     !payment.PaymentSource.Name.Contains(CashSourceTitleToken) &&
                     !payment.PaymentSource.Name.Contains(CashSourceAlternativeToken) &&
                     !payment.PaymentSource.Name.Contains(CashSourceAlternativeTitleToken) &&
                     !payment.PaymentSource.Name.Contains(CardSourceToken) &&
                     !payment.PaymentSource.Name.Contains(CardSourceTitleToken) &&
                     !payment.PaymentSource.Name.Contains(EMoneySourceToken) &&
                     !payment.PaymentSource.Name.Contains(EMoneySourceTitleToken) &&
                     !payment.PaymentSource.Name.Contains(EMoneySourceCompactToken) &&
                     !payment.PaymentSource.Name.Contains(EMoneySourceCompactTitleToken) &&
                     !payment.PaymentSource.Name.Contains(EMoneySourceRussianToken) &&
                     !payment.PaymentSource.Name.Contains(EMoneySourceRussianTitleToken) &&
                     !payment.PaymentSource.Name.Contains(EMoneySourceYuMoneyToken) &&
                     !payment.PaymentSource.Name.Contains(EMoneySourceYooMoneyToken) &&
                     !payment.PaymentSource.Name.Contains(EMoneySourceYandexToken))),
                _ => query.Where(_ => false),
            };
        }

        if (direction.HasValue)
        {
            var mappedType = direction.Value == MoneyOperationDirection.Income
                ? PaymentType.Income
                : direction.Value == MoneyOperationDirection.Expense
                    ? PaymentType.Expense
                    : (PaymentType?)null;

            if (mappedType.HasValue)
            {
                query = query.Where(payment => payment.Type == mappedType.Value);
            }
            else
            {
                query = query.Where(_ => false);
            }
        }

        if (accountingType.HasValue)
        {
            query = accountingType.Value == MoneyOperationAccountingType.Fact
                ? query.Where(payment => payment.Status == PaymentStatus.Completed)
                : query.Where(payment => payment.Status != PaymentStatus.Completed);
        }

        if (!string.IsNullOrWhiteSpace(accountName))
        {
            var accountTerm = accountName.Trim();
            query = query.Where(payment => payment.Account == accountTerm);
        }

        if (!string.IsNullOrWhiteSpace(categoryName))
        {
            var categoryTerm = categoryName.Trim();
            var categoryTitleTerm = ToTitleSearchTerm(categoryTerm);
            query = query.Where(payment =>
                (payment.IncomeType != null && payment.IncomeType.Name.Contains(categoryTerm)) ||
                (payment.ClientCase != null && (payment.ClientCase.Title ?? string.Empty).Contains(categoryTerm)) ||
                (categoryTitleTerm != categoryTerm &&
                    ((payment.IncomeType != null && payment.IncomeType.Name.Contains(categoryTitleTerm)) ||
                     (payment.ClientCase != null && (payment.ClientCase.Title ?? string.Empty).Contains(categoryTitleTerm)))));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var titleTerm = ToTitleSearchTerm(term);
            query = query.Where(payment =>
                (payment.CounterpartyName ?? string.Empty).Contains(term) ||
                (payment.Description ?? string.Empty).Contains(term) ||
                (payment.Account ?? string.Empty).Contains(term) ||
                (payment.IncomeType != null && payment.IncomeType.Name.Contains(term)) ||
                (payment.Invoice != null && payment.Invoice.Number.Contains(term)) ||
                (payment.Contract != null &&
                    ((payment.Contract.Number ?? string.Empty).Contains(term) ||
                     (payment.Contract.Title ?? string.Empty).Contains(term))) ||
                payment.Responsibles.Any(link =>
                    (link.User.FullName ?? string.Empty).Contains(term) ||
                    link.User.Email.Contains(term)) ||
                (titleTerm != term &&
                    ((payment.CounterpartyName ?? string.Empty).Contains(titleTerm) ||
                     (payment.Description ?? string.Empty).Contains(titleTerm) ||
                     (payment.Account ?? string.Empty).Contains(titleTerm) ||
                     (payment.IncomeType != null && payment.IncomeType.Name.Contains(titleTerm)) ||
                     (payment.Invoice != null && payment.Invoice.Number.Contains(titleTerm)) ||
                     (payment.Contract != null &&
                         ((payment.Contract.Number ?? string.Empty).Contains(titleTerm) ||
                          (payment.Contract.Title ?? string.Empty).Contains(titleTerm))) ||
                     payment.Responsibles.Any(link =>
                         (link.User.FullName ?? string.Empty).Contains(titleTerm) ||
                         link.User.Email.Contains(titleTerm)))));
        }

        return query;
    }

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
        return item is null ? NotFound() : Ok(item);
    }

    private async Task<string?> ApplyMoneyAccountAsync(MoneyOperation model, CancellationToken ct)
    {
        var moneyAccountId = model.MoneyAccountId.GetValueOrDefault();
        if (moneyAccountId <= 0)
        {
            model.MoneyAccountId = null;
            return null;
        }

        var account = await _db.MoneyAccounts
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == moneyAccountId, ct);
        if (account is null)
        {
            return "Денежный счёт не найден.";
        }

        if (!account.IsActive)
        {
            return "Денежный счёт отключён и не может использоваться в новой операции.";
        }

        model.Source = account.Source;
        model.AccountName = account.Name;
        if (string.IsNullOrWhiteSpace(model.Currency))
        {
            model.Currency = account.Currency;
        }

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

        var targetPaymentType = operation.Direction == MoneyOperationDirection.Expense
            ? PaymentType.Expense
            : PaymentType.Income;
        var purposeTokens = ExtractInvoiceNumberTokens(operation.Purpose)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var operationAmount = Math.Round(operation.Amount, 2, MidpointRounding.AwayFromZero);
        var operationCounterparty = operation.CounterpartyName?.Trim();

        var invoicePayments = await _db.Payments
            .AsNoTracking()
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
            .ToListAsync(ct);

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

        var candidates = invoicePayments
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
            })
            .Where(candidate => candidate.OutstandingAmount > 0m && candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.InvoiceDate)
            .ThenByDescending(candidate => candidate.InvoiceId)
            .Take(10)
            .ToList();

        return Ok(candidates);
    }

    [HttpPost("{id:int}/ignore")]
    public async Task<IActionResult> Ignore(int id, CancellationToken ct)
    {
        var operation = await _db.MoneyOperations.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (operation is null)
        {
            return NotFound();
        }

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
        return Ok(operation);
    }

    [HttpPost("{id:int}/link-invoice")]
    public async Task<IActionResult> LinkInvoice(
        int id,
        [FromBody] LinkMoneyOperationInvoiceRequest request,
        CancellationToken ct)
    {
        if (request.InvoiceId <= 0)
        {
            return BadRequest("Не выбран счёт для сверки.");
        }

        var operation = await _db.MoneyOperations.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (operation is null)
        {
            return NotFound();
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
        return Ok(operation);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] MoneyOperation model, CancellationToken ct = default)
    {
        if (model.Amount < 0)
        {
            return BadRequest("Сумма денежной операции не может быть отрицательной.");
        }

        var moneyAccountError = await ApplyMoneyAccountAsync(model, ct);
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
        model.CreatedAt = DateTime.UtcNow;

        _db.MoneyOperations.Add(model);
        await _db.SaveChangesAsync(ct);
        if (model.LinkedInvoiceId.HasValue && model.MatchStatus == MoneyOperationMatchStatus.Matched)
        {
            await ApplyMoneyOperationToInvoicePaymentAsync(model, model.LinkedInvoiceId.Value, ct);
            await _db.SaveChangesAsync(ct);
        }
        await SyncCashOrderAsync(model);
        return Created($"/api/money-operations/{model.Id}", model);
    }

    [HttpPost("transfer")]
    public async Task<ActionResult<MoneyOperationTransferResponse>> CreateTransfer(
        [FromBody] MoneyOperationTransferRequest request,
        CancellationToken ct = default)
    {
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

        var accounts = await _db.MoneyAccounts
            .Where(account => account.Id == request.SourceMoneyAccountId || account.Id == request.TargetMoneyAccountId)
            .ToListAsync(ct);
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
        var date = request.Date == default ? DateTime.Today : request.Date;
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

        _db.MoneyOperations.AddRange(sourceOperation, targetOperation);
        await _db.SaveChangesAsync(ct);
        await SyncCashOrderAsync(sourceOperation);
        await SyncCashOrderAsync(targetOperation);

        return Created(
            $"/api/money-operations/{sourceOperation.Id}",
            new MoneyOperationTransferResponse
            {
                SourceOperation = sourceOperation,
                TargetOperation = targetOperation,
            });
    }

    [HttpPost("bank-statement/import")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxStatementFileSizeBytes)]
    public async Task<ActionResult<BankStatementImportResultDto>> ImportBankStatement(IFormFile? file, CancellationToken ct)
    {
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
            return Ok(await ImportBankStatementItemsAsync(
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
            return Ok(result);
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

        return Ok(await ImportBankStatementItemsAsync(importItems, result, file.FileName, statementBytes.Length, fileHash, manualCategoryRules, ct));
    }

    [HttpPost("bank-statement/auto-import/run")]
    public async Task<ActionResult<BankStatementAutoImportRunResultDto>> RunBankStatementAutoImport(CancellationToken ct)
    {
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
                var action = await ImportBankStatement(formFile, ct);

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
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 5, 100);

        var query = _db.BankStatementImportBatches.AsNoTracking().OrderByDescending(batch => batch.CreatedAt);
        var total = await query.CountAsync(ct);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(batch => new BankStatementImportBatchListItemDto
            {
                Id = batch.Id,
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
        var batch = await _db.BankStatementImportBatches
            .AsNoTracking()
            .Where(item => item.Id == batchId)
            .Select(item => new BankStatementImportBatchDetailDto
            {
                Id = item.Id,
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
        var batch = await _db.BankStatementImportBatches
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
        var batch = await _db.BankStatementImportBatches
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
                row.Date.Date,
                row.Direction,
                row.Amount,
                row.AccountName ?? string.Empty,
                row.CounterpartyName,
                row.Purpose,
                row.PaymentOrderNumber))
            .ToList();

        return Ok(await ImportBankStatementItemsAsync(
            items,
            result,
            $"{batch.FileName} (повторная обработка)",
            batch.FileSize,
            $"{batch.FileHash}:reprocess:{batch.Id}:{DateTime.UtcNow:O}",
            await GetBankStatementManualCategoryRulesAsync(GetCurrentUserId(), ct),
            ct));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] MoneyOperation model, CancellationToken ct = default)
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

        var entity = await _db.MoneyOperations.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (entity is null)
        {
            return NotFound();
        }

        if (model.Amount < 0)
        {
            return BadRequest("Сумма денежной операции не может быть отрицательной.");
        }

        var originalLinkedInvoiceId = entity.LinkedInvoiceId;
        var originalMatchStatus = entity.MatchStatus;
        var originalAmount = entity.Amount;
        if (originalLinkedInvoiceId.HasValue)
        {
            if (model.LinkedInvoiceId != originalLinkedInvoiceId)
            {
                return BadRequest("Сверенную денежную операцию нельзя отвязать или перенести на другой счёт через редактирование.");
            }

            if (Math.Abs(originalAmount - model.Amount) > 0.009m)
            {
                return BadRequest("Сумму сверенной денежной операции нельзя изменить через редактирование.");
            }
        }

        var moneyAccountError = await ApplyMoneyAccountAsync(model, ct);
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
        entity.PaidInvoiceNumber = string.IsNullOrWhiteSpace(model.PaidInvoiceNumber) ? null : model.PaidInvoiceNumber.Trim();
        entity.PaidAmount = model.PaidAmount;
        entity.ProjectName = string.IsNullOrWhiteSpace(model.ProjectName) ? null : model.ProjectName.Trim();
        entity.DirectionName = string.IsNullOrWhiteSpace(model.DirectionName) ? null : model.DirectionName.Trim();
        entity.SalesPointName = string.IsNullOrWhiteSpace(model.SalesPointName) ? null : model.SalesPointName.Trim();
        entity.PaymentPriority = model.PaymentPriority;
        entity.PaidAccountName = string.IsNullOrWhiteSpace(model.PaidAccountName) ? null : model.PaidAccountName.Trim();
        entity.MatchComment = string.IsNullOrWhiteSpace(model.MatchComment) ? null : model.MatchComment.Trim();
        if (model.LinkedInvoiceId.HasValue)
        {
            var invoice = await _db.Invoices.AsNoTracking().FirstOrDefaultAsync(item => item.Id == model.LinkedInvoiceId.Value);
            if (invoice is null)
            {
                return BadRequest("Счёт для сверки банковской операции не найден.");
            }

            var validation = await ValidateInvoiceLinkAsync(entity, invoice.Id);
            if (validation.Error is not null)
            {
                return BadRequest(validation.Error);
            }

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
        else
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
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        await SyncCashOrderAsync(entity);
        return Ok(entity);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
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

        var entity = await _db.MoneyOperations.FindAsync([id], ct);
        if (entity is null)
        {
            return NotFound();
        }

        if (IsAppliedToInvoice(entity))
        {
            return BadRequest("Связанную со счётом денежную операцию нельзя удалить без операции сторно.");
        }

        var linkedOrders = await _db.CashOrders
            .Where(order => order.MoneyOperationId == entity.Id && order.Status != CashOrderStatus.Cancelled)
            .ToListAsync(ct);
        foreach (var order in linkedOrders)
        {
            order.Status = CashOrderStatus.Cancelled;
            order.UpdatedAt = DateTime.UtcNow;
        }

        _db.MoneyOperations.Remove(entity);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

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

    private static string ToTitleSearchTerm(string value)
        => string.IsNullOrWhiteSpace(value)
            ? value
            : string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Length == 1
                    ? part.ToUpperInvariant()
                    : char.ToUpperInvariant(part[0]) + part[1..]));

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
        => new()
        {
            Id = row.Id,
            Date = row.Date,
            Source = row.Source,
            AccountName = row.AccountName,
            OpeningBalance = row.OpeningBalance,
            Income = row.Income,
            Expense = row.Expense,
            ClosingBalance = row.ClosingBalance,
            IncomeCount = row.IncomeCount,
            ExpenseCount = row.ExpenseCount,
            IsBalanced = row.IsBalanced,
            IssueCount = row.IssueCount,
            CheckedAt = row.CheckedAt,
            CheckedByUserId = row.CheckedByUserId,
        };

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

    private static bool IsBankPaymentSource(string? sourceName)
        => string.IsNullOrWhiteSpace(sourceName) ||
           (!sourceName.Contains(CashSourceToken) &&
            !sourceName.Contains(CashSourceTitleToken) &&
           !sourceName.Contains(CashSourceAlternativeToken) &&
           !sourceName.Contains(CashSourceAlternativeTitleToken) &&
            !sourceName.Contains(CardSourceToken) &&
            !sourceName.Contains(CardSourceTitleToken) &&
            !sourceName.Contains(EMoneySourceToken) &&
            !sourceName.Contains(EMoneySourceTitleToken) &&
            !sourceName.Contains(EMoneySourceCompactToken) &&
            !sourceName.Contains(EMoneySourceCompactTitleToken) &&
            !sourceName.Contains(EMoneySourceRussianToken) &&
            !sourceName.Contains(EMoneySourceRussianTitleToken) &&
            !sourceName.Contains(EMoneySourceYuMoneyToken) &&
            !sourceName.Contains(EMoneySourceYooMoneyToken) &&
            !sourceName.Contains(EMoneySourceYandexToken));

    private async Task<BankStatementImportResultDto> ImportBankStatementItemsAsync(
        IReadOnlyCollection<BankStatementImportItem> items,
        BankStatementImportResultDto result,
        string fileName,
        long fileSize,
        string fileHash,
        IReadOnlyCollection<BankStatementCategoryRule> manualCategoryRules,
        CancellationToken ct)
    {
        if (items.Count == 0)
        {
            return result;
        }

        var minDate = items.Min(item => item.Date);
        var maxDate = items.Max(item => item.Date);
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        var batch = new BankStatementImportBatch
        {
            FileName = Path.GetFileName(fileName),
            FileHash = fileHash,
            FileSize = fileSize,
            RowsRead = result.RowsRead,
            ErrorRows = result.Errors.Count,
            ErrorSummary = result.Errors.Count == 0 ? null : string.Join(Environment.NewLine, result.Errors.Take(50)),
            CreatedAt = DateTime.UtcNow,
        };
        _db.BankStatementImportBatches.Add(batch);

        var importKeys = new HashSet<string>(StringComparer.Ordinal);
        var pendingRows = new List<BankStatementImportRow>();
        var pendingOperations = new List<MoneyOperation>();
        var existingOperations = await _db.MoneyOperations
            .AsNoTracking()
            .Where(operation =>
                operation.Source == MoneyOperationSource.Bank &&
                operation.AccountingType == MoneyOperationAccountingType.Fact &&
                operation.Date >= minDate &&
                operation.Date <= maxDate)
            .ToListAsync(ct);
        var completedPayments = await _db.Payments
            .AsNoTracking()
            .Include(payment => payment.PaymentSource)
            .Where(payment =>
                payment.Status == PaymentStatus.Completed &&
                (payment.LastPaymentDate ?? payment.PaidDate ?? payment.Date) >= minDate &&
                (payment.LastPaymentDate ?? payment.PaidDate ?? payment.Date) <= maxDate)
            .ToListAsync(ct);
        var moneyAccountsByName = await _db.MoneyAccounts
            .AsNoTracking()
            .Where(account => account.Source == MoneyOperationSource.Bank && account.IsActive)
            .ToDictionaryAsync(account => account.Name, StringComparer.OrdinalIgnoreCase, ct);
        var incomeTypeCandidates = await _db.IncomeTypes
            .AsNoTracking()
            .Where(incomeType => incomeType.IsActive)
            .Select(incomeType => new BankStatementIncomeTypeCandidate(
                incomeType.Id,
                incomeType.Name,
                incomeType.PaymentType))
            .ToListAsync(ct);
        var invoiceNumbersFromStatement = items
            .SelectMany(item => ExtractInvoiceNumberTokens(item.Purpose))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var statementPaymentTypes = items
            .Select(item => item.Direction == MoneyOperationDirection.Expense ? PaymentType.Expense : PaymentType.Income)
            .Distinct()
            .ToList();
        var invoiceCandidates = (await _db.Payments
                .AsNoTracking()
                .Include(payment => payment.Invoice)
                .Include(payment => payment.Client)
                .Include(payment => payment.ExpenseClient)
                .Include(payment => payment.ExpenseLegalEntity)
                .Include(payment => payment.Contract)
                .Where(payment =>
                    payment.DocumentKind == DocumentKind.Invoice &&
                    statementPaymentTypes.Contains(payment.Type) &&
                    payment.Status != PaymentStatus.Cancelled &&
                    payment.InvoiceId.HasValue)
                .ToListAsync(ct))
                .Select(payment => new BankStatementInvoiceCandidate(
                    payment.Id,
                    payment.InvoiceId!.Value,
                    payment.Invoice?.Number ?? payment.Account ?? string.Empty,
                    payment.Account,
                    payment.Amount,
                    payment.PaidAmount,
                    payment.Status,
                    payment.Type,
                    payment.Type == PaymentType.Expense ? payment.ExpenseClientId ?? payment.ClientId : payment.ClientId,
                    ResolveInvoiceCandidateCounterpartyName(payment),
                    payment.CounterpartyName,
                    BuildInvoiceNumberTokenSet(payment.Invoice?.Number, payment.Account, payment.LegacyAccountNumber),
                    BuildInvoiceNumberTokenSet(payment.Contract?.Number)))
                .Where(candidate =>
                    invoiceNumbersFromStatement.Count == 0 ||
                    candidate.MatchingNumberTokens.Any(invoiceNumbersFromStatement.Contains) ||
                    items.Any(item =>
                        item.Direction == MoneyOperationDirection.Income &&
                        Math.Abs(candidate.OutstandingAmount - item.Amount) <= 0.009m &&
                        NamesLookRelated(item.CounterpartyName, candidate.ClientName ?? candidate.CounterpartyName)))
                .ToList();

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var importKey = BuildBankStatementImportKey(
                item.Date,
                item.Direction,
                item.Amount,
                item.DocumentNumber,
                item.CounterpartyName);

            if (!importKeys.Add(importKey))
            {
                result.Skipped++;
                pendingRows.Add(CreateImportRow(batch, item, importKey, BankStatementImportRowStatus.Skipped, "Повтор строки внутри файла."));
                continue;
            }

            var alreadyExists = existingOperations.Any(operation =>
                operation.Direction == item.Direction &&
                operation.Date == item.Date &&
                operation.Amount == item.Amount &&
                (operation.PaymentOrderNumber ?? string.Empty) == (item.DocumentNumber ?? string.Empty) &&
                (operation.CounterpartyName ?? string.Empty) == (item.CounterpartyName ?? string.Empty));

            if (alreadyExists)
            {
                result.Skipped++;
                pendingRows.Add(CreateImportRow(batch, item, importKey, BankStatementImportRowStatus.Skipped, "Такая банковская операция уже есть в журнале."));
                continue;
            }

            var paymentType = item.Direction == MoneyOperationDirection.Income
                ? PaymentType.Income
                : PaymentType.Expense;
            var matchingPaymentExists = completedPayments.Any(payment =>
                payment.Type == paymentType &&
                (payment.PaidAmount > 0m ? payment.PaidAmount : payment.Amount) == item.Amount &&
                (payment.LastPaymentDate ?? payment.PaidDate ?? payment.Date).Date == item.Date &&
                string.Equals(payment.PaymentOrderNumber ?? string.Empty, item.DocumentNumber ?? string.Empty, StringComparison.Ordinal) &&
                string.Equals(payment.CounterpartyName ?? string.Empty, item.CounterpartyName ?? string.Empty, StringComparison.Ordinal) &&
                IsBankPaymentSource(payment.PaymentSource?.Name));

            if (matchingPaymentExists)
            {
                result.Skipped++;
                pendingRows.Add(CreateImportRow(batch, item, importKey, BankStatementImportRowStatus.Skipped, "Найден уже выполненный банковский платёж."));
                continue;
            }

            var incomeTypeMatch = ResolveBankStatementIncomeType(item, incomeTypeCandidates, manualCategoryRules);
            var categoryName = incomeTypeMatch is null
                ? ResolveBankStatementCategoryName(item, manualCategoryRules)
                : null;
            var operation = new MoneyOperation
            {
                Source = MoneyOperationSource.Bank,
                Direction = item.Direction,
                AccountingType = MoneyOperationAccountingType.Fact,
                AccountName = item.AccountName,
                CounterpartyName = item.CounterpartyName,
                IncomeTypeId = incomeTypeMatch?.Id,
                CategoryName = categoryName,
                Amount = item.Amount,
                Currency = "RUB",
                PaymentOrderNumber = item.DocumentNumber,
                Date = item.Date,
                RecognitionDate = item.Date,
                Purpose = item.Purpose,
                MatchStatus = MoneyOperationMatchStatus.ClarificationRequired,
                BankStatementImportBatch = batch,
                CreatedAt = DateTime.UtcNow,
            };
            if (!string.IsNullOrWhiteSpace(item.AccountName) &&
                moneyAccountsByName.TryGetValue(item.AccountName.Trim(), out var moneyAccount))
            {
                operation.MoneyAccountId = moneyAccount.Id;
                operation.AccountName = moneyAccount.Name;
                operation.Currency = moneyAccount.Currency;
            }

            var autoMatch = TryResolveBankStatementInvoiceMatch(item, invoiceCandidates);
            var importRowMessage = (string?)null;
            if (autoMatch is not null)
            {
                operation.LinkedInvoiceId = autoMatch.InvoiceId;
                operation.PaidInvoiceNumber = autoMatch.InvoiceNumber;
                operation.PaidAccountName = autoMatch.InvoiceNumber;
                operation.MatchStatus = MoneyOperationMatchStatus.Matched;
                operation.MatchedAt = DateTime.UtcNow;
                operation.MatchComment = BuildBankStatementAutoMatchComment(item, autoMatch);
                importRowMessage = operation.MatchComment;
            }

            _db.MoneyOperations.Add(operation);
            pendingOperations.Add(operation);
            pendingRows.Add(CreateImportRow(batch, item, importKey, BankStatementImportRowStatus.Created, importRowMessage, operation));

            result.Created++;
            result.Operations.Add(new BankStatementImportCreatedOperationDto
            {
                Date = operation.Date,
                Direction = operation.Direction,
                Amount = operation.Amount,
                CounterpartyName = operation.CounterpartyName,
                PaymentOrderNumber = operation.PaymentOrderNumber,
                MatchStatus = operation.MatchStatus,
            });
        }

        batch.CreatedRows = result.Created;
        batch.SkippedRows = result.Skipped;
        batch.ErrorRows = result.Errors.Count;
        batch.Status = result.Errors.Count > 0
            ? BankStatementImportBatchStatus.AppliedWithErrors
            : BankStatementImportBatchStatus.Applied;
        _db.BankStatementImportRows.AddRange(pendingRows);

        if (pendingOperations.Count > 0)
        {
            await _db.SaveChangesAsync(ct);
            var createdIndex = 0;
            for (var index = 0; index < result.Operations.Count; index++)
            {
                result.Operations[index].Id = pendingOperations[createdIndex].Id;
                createdIndex++;
            }

            foreach (var operation in pendingOperations.Where(item =>
                         item.LinkedInvoiceId.HasValue && item.MatchStatus == MoneyOperationMatchStatus.Matched))
            {
                await ApplyMoneyOperationToInvoicePaymentAsync(operation, operation.LinkedInvoiceId!.Value, ct);
            }

            await _db.SaveChangesAsync(ct);
        }
        else
        {
            await _db.SaveChangesAsync(ct);
        }

        result.BatchId = batch.Id;

        await transaction.CommitAsync(ct);
        return result;
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

    private static BankStatementCategoryRule? TryCreateManualBankStatementCategoryRule(
        BankStatementManualCategoryRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.CategoryName) || string.IsNullOrWhiteSpace(rule.Keywords))
        {
            return null;
        }

        var paymentType = rule.PaymentType == PaymentType.Expense ? PaymentType.Expense : PaymentType.Income;
        var keywords = rule.Keywords
            .Split(new[] { ';', ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(keyword => NormalizeNameForMatch(keyword).Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToArray();
        if (keywords.Length == 0)
        {
            return null;
        }

        var incomeTypeId = rule.IncomeTypeId.GetValueOrDefault() > 0 ? rule.IncomeTypeId : null;
        return new BankStatementCategoryRule(
            rule.CategoryName.Trim(),
            paymentType,
            keywords,
            incomeTypeId,
            IsManual: true);
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

    private static BankStatementImportRow CreateImportRow(
        BankStatementImportBatch batch,
        BankStatementImportItem item,
        string importKey,
        string status,
        string? message,
        MoneyOperation? operation = null)
        => new()
        {
            Batch = batch,
            RowNumber = item.RowNumber,
            ImportKey = importKey,
            Status = status,
            Message = message,
            MoneyOperation = operation,
            Date = item.Date,
            Direction = item.Direction,
            Amount = item.Amount,
            AccountName = item.AccountName,
            CounterpartyName = item.CounterpartyName,
            Purpose = item.Purpose,
            PaymentOrderNumber = item.DocumentNumber,
            CreatedAt = DateTime.UtcNow,
        };

    private static bool LooksLikeClientBankExchange(string text)
        => text.Contains("1CClientBankExchange", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("СекцияДокумент=", StringComparison.OrdinalIgnoreCase);

    private static (List<BankStatementImportItem> Items, BankStatementImportResultDto Result) ParseClientBankExchange(string text)
    {
        var items = new List<BankStatementImportItem>();
        var result = new BankStatementImportResultDto();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var statementAccount = string.Empty;
        Dictionary<string, string>? document = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("СекцияДокумент=", StringComparison.OrdinalIgnoreCase))
            {
                document = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }

            if (line.Equals("КонецДокумента", StringComparison.OrdinalIgnoreCase))
            {
                if (document is not null)
                {
                    AddClientBankExchangeDocument(document, statementAccount, items, result);
                    document = null;
                }

                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();
            if (document is not null)
            {
                document[key] = value;
            }
            else if (key.Equals("РасчСчет", StringComparison.OrdinalIgnoreCase))
            {
                statementAccount = NormalizeBankAccount(value);
            }
        }

        if (document is not null)
        {
            AddClientBankExchangeDocument(document, statementAccount, items, result);
        }

        return (items, result);
    }

    private static void AddClientBankExchangeDocument(
        IReadOnlyDictionary<string, string> document,
        string statementAccount,
        ICollection<BankStatementImportItem> items,
        BankStatementImportResultDto result)
    {
        result.RowsRead++;
        var documentNumber = NormalizeStatementText(GetDictionaryValue(document, "Номер"));

        if (!TryParseStatementDate(GetDictionaryValue(document, "Дата") ?? string.Empty, out var date))
        {
            result.Skipped++;
            result.Errors.Add($"Документ {documentNumber ?? result.RowsRead.ToString(CultureInfo.InvariantCulture)}: не удалось прочитать дату.");
            return;
        }

        if (!TryParseStatementAmount(GetDictionaryValue(document, "Сумма") ?? string.Empty, out var amount) || amount == 0m)
        {
            result.Skipped++;
            result.Errors.Add($"Документ {documentNumber ?? result.RowsRead.ToString(CultureInfo.InvariantCulture)}: не удалось прочитать сумму.");
            return;
        }

        var payerAccount = NormalizeBankAccount(GetDictionaryValue(document, "ПлательщикСчет"));
        var recipientAccount = NormalizeBankAccount(GetDictionaryValue(document, "ПолучательСчет"));
        var isExpense = !string.IsNullOrWhiteSpace(statementAccount) &&
                        string.Equals(statementAccount, payerAccount, StringComparison.Ordinal);
        var isIncome = !string.IsNullOrWhiteSpace(statementAccount) &&
                       string.Equals(statementAccount, recipientAccount, StringComparison.Ordinal);
        var direction = isExpense && !isIncome ? MoneyOperationDirection.Expense : MoneyOperationDirection.Income;
        var counterparty = NormalizeStatementText(direction == MoneyOperationDirection.Expense
            ? GetDictionaryValue(document, "Получатель")
            : GetDictionaryValue(document, "Плательщик"));
        var purpose = NormalizeStatementText(GetDictionaryValue(document, "НазначениеПлатежа"));
        var account = statementAccount;
        if (string.IsNullOrWhiteSpace(account))
        {
            account = direction == MoneyOperationDirection.Expense ? payerAccount : recipientAccount;
        }

        items.Add(new BankStatementImportItem(
            result.RowsRead,
            date.Date,
            direction,
            Math.Abs(amount),
            string.IsNullOrWhiteSpace(account) ? "Расчетный счет" : account,
            counterparty,
            purpose,
            documentNumber));
    }

    private static string? GetDictionaryValue(IReadOnlyDictionary<string, string> values, string key)
        => values.TryGetValue(key, out var value) ? value : null;

    private static string NormalizeBankAccount(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().Replace(" ", string.Empty);

    private static string DecodeStatementBytes(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        var utf8 = new UTF8Encoding(false, true);
        try
        {
            return utf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1251).GetString(bytes);
        }
    }

    private static List<List<string>> ParseDelimitedRows(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var firstLine = normalized.Split('\n', 2)[0];
        var delimiter = firstLine.Count(ch => ch == ';') >= firstLine.Count(ch => ch == '\t') ? ';' : '\t';
        if (firstLine.Count(ch => ch == ',') > firstLine.Count(ch => ch == delimiter))
        {
            delimiter = ',';
        }

        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;

        for (var index = 0; index < normalized.Length; index++)
        {
            var ch = normalized[index];
            if (ch == '"')
            {
                if (inQuotes && index + 1 < normalized.Length && normalized[index + 1] == '"')
                {
                    cell.Append('"');
                    index++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                continue;
            }

            if (!inQuotes && ch == delimiter)
            {
                row.Add(cell.ToString().Trim());
                cell.Clear();
                continue;
            }

            if (!inQuotes && ch == '\n')
            {
                row.Add(cell.ToString().Trim());
                cell.Clear();
                rows.Add(row);
                row = new List<string>();
                continue;
            }

            cell.Append(ch);
        }

        row.Add(cell.ToString().Trim());
        if (row.Any(value => !string.IsNullOrWhiteSpace(value)))
        {
            rows.Add(row);
        }

        return rows;
    }

    private static StatementHeaderMap BuildStatementHeaderMap(IReadOnlyList<string> header)
    {
        var map = new StatementHeaderMap();
        for (var index = 0; index < header.Count; index++)
        {
            var name = NormalizeHeaderName(header[index]);
            if (map.Date < 0 && (name.Contains("дата") || name.Contains("date")))
            {
                map.Date = index;
            }
            else if (map.DocumentNumber < 0 && (name.Contains("номер") || name.Contains("документ") || name.Contains("поручение")))
            {
                map.DocumentNumber = index;
            }
            else if (map.Counterparty < 0 && (name.Contains("контрагент") || name.Contains("плательщик") || name.Contains("получатель") || name.Contains("payer")))
            {
                map.Counterparty = index;
            }
            else if (map.Purpose < 0 && (name.Contains("назначение") || name.Contains("комментар") || name.Contains("purpose")))
            {
                map.Purpose = index;
            }
            else if (map.Account < 0 && (name.Contains("счет") || name.Contains("счёт") || name.Contains("account")))
            {
                map.Account = index;
            }
            else if (map.Income < 0 && (name.Contains("приход") || name.Contains("поступ") || name.Contains("кредит") || name.Contains("credit")))
            {
                map.Income = index;
            }
            else if (map.Expense < 0 && (name.Contains("расход") || name.Contains("спис") || name.Contains("дебет") || name.Contains("debit")))
            {
                map.Expense = index;
            }
            else if (map.Amount < 0 && (name.Contains("сумма") || name.Contains("amount")))
            {
                map.Amount = index;
            }
        }

        return map;
    }

    private static string NormalizeHeaderName(string value)
        => value.Trim().ToLowerInvariant().Replace(" ", string.Empty).Replace("_", string.Empty);

    private static bool TryGetCell(IReadOnlyList<string> row, int index, out string value)
    {
        if (index >= 0 && index < row.Count)
        {
            value = row[index].Trim();
            return !string.IsNullOrWhiteSpace(value);
        }

        value = string.Empty;
        return false;
    }

    private static string? GetOptionalCell(IReadOnlyList<string> row, int index)
        => TryGetCell(row, index, out var value) ? value : null;

    private static string? NormalizeStatementText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string BuildBankStatementImportKey(
        DateTime date,
        MoneyOperationDirection direction,
        decimal amount,
        string? documentNumber,
        string? counterparty)
        => string.Join("|", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), direction, amount.ToString("0.00", CultureInfo.InvariantCulture), documentNumber ?? string.Empty, counterparty ?? string.Empty);

    private static BankStatementInvoiceCandidate? TryResolveBankStatementInvoiceMatch(
        BankStatementImportItem item,
        IReadOnlyCollection<BankStatementInvoiceCandidate> candidates)
    {
        if (item.Direction is not (MoneyOperationDirection.Income or MoneyOperationDirection.Expense))
        {
            return null;
        }

        var targetPaymentType = item.Direction == MoneyOperationDirection.Expense
            ? PaymentType.Expense
            : PaymentType.Income;
        var tokens = ExtractInvoiceNumberTokens(item.Purpose).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (tokens.Count > 0)
        {
            var matchingCandidates = candidates
                .Where(candidate =>
                    candidate.PaymentType == targetPaymentType &&
                    candidate.OutstandingAmount > 0m &&
                    item.Amount <= candidate.OutstandingAmount + 0.009m &&
                    candidate.MatchingNumberTokens.Any(tokens.Contains))
                .GroupBy(candidate => candidate.InvoiceId)
                .Select(group => group.First())
                .ToList();

            if (matchingCandidates.Count == 1)
            {
                return matchingCandidates[0];
            }

            var exactAmountCandidates = matchingCandidates
                .Where(candidate => Math.Abs(candidate.OutstandingAmount - item.Amount) <= 0.009m)
                .ToList();
            if (exactAmountCandidates.Count == 1)
            {
                return exactAmountCandidates[0];
            }
        }

        var exactNamedCandidates = candidates
            .Where(candidate =>
                candidate.PaymentType == targetPaymentType &&
                candidate.OutstandingAmount > 0m &&
                Math.Abs(candidate.OutstandingAmount - item.Amount) <= 0.009m &&
                NamesLookRelated(item.CounterpartyName, candidate.ClientName ?? candidate.CounterpartyName))
            .GroupBy(candidate => candidate.InvoiceId)
            .Select(group => group.First())
            .ToList();
        return exactNamedCandidates.Count == 1 ? exactNamedCandidates[0] : null;
    }

    private static string BuildBankStatementAutoMatchComment(BankStatementImportItem item, BankStatementInvoiceCandidate candidate)
    {
        var tokens = ExtractInvoiceNumberTokens(item.Purpose).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (tokens.Count > 0 && candidate.InvoiceNumberTokens.Any(tokens.Contains))
        {
            return $"Автоматически сверено по номеру счёта {candidate.InvoiceNumber} из назначения платежа.";
        }

        if (tokens.Count > 0 && candidate.ContractNumberTokens.Any(tokens.Contains))
        {
            return $"Автоматически сверено со счётом {candidate.InvoiceNumber} по номеру договора из назначения платежа.";
        }

        return candidate.PaymentType == PaymentType.Expense
            ? $"Автоматически сверено со счётом {candidate.InvoiceNumber} по сумме и поставщику из выписки."
            : $"Автоматически сверено со счётом {candidate.InvoiceNumber} по сумме и клиенту из выписки.";
    }

    private static BankStatementIncomeTypeCandidate? ResolveBankStatementIncomeType(
        BankStatementImportItem item,
        IReadOnlyCollection<BankStatementIncomeTypeCandidate> candidates,
        IReadOnlyCollection<BankStatementCategoryRule> manualCategoryRules)
    {
        var paymentType = item.Direction == MoneyOperationDirection.Income
            ? PaymentType.Income
            : PaymentType.Expense;
        var haystack = BuildBankStatementMatchHaystack(item);
        if (haystack.Length < 3)
        {
            return null;
        }

        var categoryRule = ResolveBankStatementCategoryRule(paymentType, haystack, manualCategoryRules);
        var matches = candidates
            .Where(candidate => candidate.PaymentType == paymentType)
            .Select(candidate => new
            {
                Candidate = candidate,
                Score = ScoreBankStatementIncomeTypeCandidate(candidate, haystack, categoryRule),
            })
            .Where(match => match.Score > 0)
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Candidate.Name)
            .ToList();

        if (matches.Count == 0)
        {
            return null;
        }

        return matches.Count == 1 || matches[0].Score > matches[1].Score
            ? matches[0].Candidate
            : null;
    }

    private static string ResolveBankStatementCategoryName(
        BankStatementImportItem item,
        IReadOnlyCollection<BankStatementCategoryRule> manualCategoryRules)
    {
        var paymentType = item.Direction == MoneyOperationDirection.Income
            ? PaymentType.Income
            : PaymentType.Expense;
        var haystack = BuildBankStatementMatchHaystack(item);
        return ResolveBankStatementCategoryRule(paymentType, haystack, manualCategoryRules)?.CategoryName
               ?? (item.Direction == MoneyOperationDirection.Income ? "Поступление по выписке" : "Списание по выписке");
    }

    private static BankStatementCategoryRule? ResolveBankStatementCategoryRule(
        PaymentType paymentType,
        string haystack,
        IReadOnlyCollection<BankStatementCategoryRule> manualCategoryRules)
    {
        if (haystack.Length < 3)
        {
            return null;
        }

        var matches = manualCategoryRules
            .Concat(BankStatementCategoryRules)
            .Where(rule => rule.PaymentType == paymentType)
            .Select(rule => new
            {
                Rule = rule,
                Score = rule.Keywords.Max(keyword => ScoreBankStatementKeyword(keyword, haystack)),
            })
            .Where(match => match.Score > 0)
            .OrderByDescending(match => match.Score)
            .ThenByDescending(match => match.Rule.IsManual)
            .ThenBy(match => match.Rule.CategoryName)
            .ToList();

        return matches.Count == 0
            ? null
            : matches[0].Rule;
    }

    private static int ScoreBankStatementIncomeTypeCandidate(
        BankStatementIncomeTypeCandidate candidate,
        string haystack,
        BankStatementCategoryRule? categoryRule)
    {
        var directScore = ScoreBankStatementIncomeType(candidate.Name, haystack);
        if (categoryRule is null)
        {
            return directScore;
        }

        if (categoryRule.IncomeTypeId == candidate.Id)
        {
            return Math.Max(directScore, 2200);
        }

        var categoryScore = ScoreBankStatementIncomeType(candidate.Name, NormalizeNameForMatch(categoryRule.CategoryName));
        return Math.Max(directScore, categoryScore > 0 ? 800 + categoryScore : 0);
    }

    private static int ScoreBankStatementIncomeType(string name, string haystack)
    {
        var normalizedName = NormalizeNameForMatch(name);
        if (normalizedName.Length < 3)
        {
            return 0;
        }

        if (haystack.Contains(normalizedName, StringComparison.Ordinal))
        {
            return 1000 + normalizedName.Length;
        }

        var words = normalizedName
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.Length >= 3)
            .ToArray();
        return words.Length > 1 && words.All(word => haystack.Contains(word, StringComparison.Ordinal))
            ? 500 + words.Sum(word => word.Length)
            : 0;
    }

    private static int ScoreBankStatementKeyword(string keyword, string haystack)
    {
        var normalizedKeyword = NormalizeNameForMatch(keyword);
        if (normalizedKeyword.Length < 3)
        {
            return 0;
        }

        if (haystack.Contains(normalizedKeyword, StringComparison.Ordinal))
        {
            return 1000 + normalizedKeyword.Length;
        }

        var words = normalizedKeyword
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.Length >= 3)
            .ToArray();
        return words.Length > 1 && words.All(word => haystack.Contains(word, StringComparison.Ordinal))
            ? 500 + words.Sum(word => word.Length)
            : 0;
    }

    private static string BuildBankStatementMatchHaystack(BankStatementImportItem item)
        => NormalizeNameForMatch(string.Join(' ', new[] { item.Purpose, item.CounterpartyName }
            .Where(value => !string.IsNullOrWhiteSpace(value))));

    private static IEnumerable<string> ExtractInvoiceNumberTokens(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        foreach (Match match in Regex.Matches(
                     text,
                     @"(?:сч[её]т\w*|договор\w*|contract|agreement|invoice|inv)\s*(?:№|#|n|no\.?)?\s*([A-Za-zА-Яа-я0-9][A-Za-zА-Яа-я0-9\/\-_]*)",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                     TimeSpan.FromMilliseconds(100)))
        {
            var token = NormalizeInvoiceNumberToken(match.Groups[1].Value);
            if (!string.IsNullOrWhiteSpace(token))
            {
                yield return token;
            }
        }

        foreach (Match match in Regex.Matches(
                     text,
                     @"(?:№|#)\s*([A-Za-zА-Яа-я0-9][A-Za-zА-Яа-я0-9\/\-_]*)",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                     TimeSpan.FromMilliseconds(100)))
        {
            var token = NormalizeInvoiceNumberToken(match.Groups[1].Value);
            if (!string.IsNullOrWhiteSpace(token))
            {
                yield return token;
            }
        }
    }

    private static HashSet<string> BuildInvoiceNumberTokenSet(params string?[] values)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            var token = NormalizeInvoiceNumberToken(value);
            if (!string.IsNullOrWhiteSpace(token))
            {
                tokens.Add(token);
            }
        }

        return tokens;
    }

    private static string? NormalizeInvoiceNumberToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().Trim('№', '#', '.', ',', ';', ':', ')', '(', '[', ']');
        normalized = Regex.Replace(normalized, @"\s+", string.Empty, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        normalized = normalized.ToLowerInvariant();
        if (!normalized.All(char.IsDigit))
        {
            return normalized;
        }

        var withoutLeadingZeroes = normalized.TrimStart('0');
        return withoutLeadingZeroes.Length == 0 ? "0" : withoutLeadingZeroes;
    }

    private static decimal CalculateOutstandingAmount(decimal amount, decimal paidAmount, PaymentStatus status)
        => status == PaymentStatus.Completed
            ? 0m
            : Math.Round(Math.Max(amount - Math.Min(Math.Max(paidAmount, 0m), amount), 0m), 2, MidpointRounding.AwayFromZero);

    private async Task<InvoiceLinkValidationResult> ValidateInvoiceLinkAsync(
        MoneyOperation operation,
        int invoiceId,
        CancellationToken ct = default)
    {
        var invoice = await _db.Invoices
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == invoiceId, ct);
        if (invoice is null)
        {
            return new(null, "Счёт для сверки денежной операции не найден.");
        }

        if (operation.Direction != MoneyOperationDirection.Income)
        {
            return new(invoice, "К счёту можно привязать только приходную денежную операцию.");
        }

        var invoicePayments = await _db.Payments
            .AsNoTracking()
            .Where(payment =>
                payment.InvoiceId == invoiceId &&
                payment.Type == PaymentType.Income &&
                payment.Status != PaymentStatus.Cancelled)
            .ToListAsync(ct);
        var invoicePayment = invoicePayments
            .Where(payment => payment.DocumentKind == DocumentKind.Invoice)
            .OrderByDescending(payment => payment.Amount)
            .ThenBy(payment => payment.Id)
            .FirstOrDefault();
        if (invoicePayment is null)
        {
            return new(invoice, null);
        }

        var statusSourcePayments = InvoicePaymentStateService.SelectStatusSourcePayments(
            invoicePayments,
            invoicePayment.Id,
            invoicePayment.Amount);
        var outstandingAmount = statusSourcePayments.Count > 0
            ? Math.Round(statusSourcePayments.Sum(InvoicePaymentStateService.ResolveOutstandingAmount), 2, MidpointRounding.AwayFromZero)
            : CalculateOutstandingAmount(invoicePayment.Amount, invoicePayment.PaidAmount, invoicePayment.Status);
        if (outstandingAmount <= 0m)
        {
            return new(invoice, "Счёт уже полностью оплачен, привязка денежной операции не выполнена.");
        }

        if (operation.Amount > outstandingAmount + 0.009m &&
            (invoicePayment.Type != PaymentType.Income || !invoicePayment.ClientId.HasValue))
        {
            var overpayMessage = invoicePayment.Type == PaymentType.Expense
                ? $"Сумма денежной операции больше остатка по расходному счёту. Остаток: {outstandingAmount:0.00}."
                : $"Сумма денежной операции больше остатка по счёту, а клиент счёта не указан для зачисления переплаты. Остаток: {outstandingAmount:0.00}.";
            return new(invoice, overpayMessage);
        }

        return new(invoice, null);
    }

    private async Task ApplyMoneyOperationToInvoicePaymentAsync(
        MoneyOperation operation,
        int invoiceId,
        CancellationToken ct = default)
    {
        if (operation.Direction is not (MoneyOperationDirection.Income or MoneyOperationDirection.Expense) || operation.Amount <= 0m)
        {
            return;
        }

        var targetPaymentType = operation.Direction == MoneyOperationDirection.Expense
            ? PaymentType.Expense
            : PaymentType.Income;
        var invoicePayments = await _db.Payments
            .Where(item =>
                item.InvoiceId == invoiceId &&
                item.Type == targetPaymentType &&
                item.Status != PaymentStatus.Cancelled)
            .ToListAsync(ct);
        var representative = invoicePayments
            .Where(item => item.DocumentKind == DocumentKind.Invoice)
            .OrderByDescending(item => item.Amount)
            .ThenBy(item => item.Id)
            .FirstOrDefault();
        if (representative is null)
        {
            return;
        }

        var sourcePayments = InvoicePaymentStateService.SelectStatusSourcePayments(
            invoicePayments,
            representative.Id,
            representative.Amount);
        var payment = sourcePayments
            .Where(item => item.Status != PaymentStatus.Cancelled)
            .Where(item => CalculateOutstandingAmount(item.Amount, item.PaidAmount, item.Status) > 0m)
            .OrderBy(InvoicePaymentStateService.EffectivePlannedDate)
            .ThenBy(item => item.Id)
            .FirstOrDefault()
            ?? (CalculateOutstandingAmount(representative.Amount, representative.PaidAmount, representative.Status) > 0m
                ? representative
                : null);
        if (payment is null)
        {
            return;
        }

        var currentPaidAmount = Math.Max(payment.PaidAmount, 0m);
        var outstandingAmount = CalculateOutstandingAmount(payment.Amount, currentPaidAmount, payment.Status);
        if (outstandingAmount <= 0m)
        {
            return;
        }

        var appliedAmount = Math.Round(Math.Min(operation.Amount, outstandingAmount), 2, MidpointRounding.AwayFromZero);
        if (appliedAmount <= 0m)
        {
            return;
        }
        var overpaymentAmount = Math.Round(operation.Amount - appliedAmount, 2, MidpointRounding.AwayFromZero);

        payment.PaidAmount = Math.Round(currentPaidAmount + appliedAmount, 2, MidpointRounding.AwayFromZero);
        payment.LastPaymentDate = operation.Date;
        payment.PaymentDocumentDate ??= operation.RecognitionDate ?? operation.Date;
        payment.PaymentOrderNumber ??= operation.PaymentOrderNumber;

        if (payment.PaidAmount + 0.009m >= payment.Amount)
        {
            payment.PaidAmount = payment.Amount;
            payment.Status = PaymentStatus.Completed;
            payment.IsPaid = true;
            payment.PaidDate ??= operation.Date;
        }
        else
        {
            payment.Status = PaymentStatus.Processing;
            payment.IsPaid = false;
            payment.PaidDate = null;
        }

        var sourceLabel = operation.Source switch
        {
            MoneyOperationSource.Cash => "касса",
            MoneyOperationSource.Card => "карты",
            MoneyOperationSource.EMoney => "e-money",
            _ => "банк",
        };
        var movementLabel = targetPaymentType == PaymentType.Expense ? "списание" : "поступление";
        var note = $"Системно: {DateTime.UtcNow:dd.MM.yyyy HH:mm:ss} — зачтено {movementLabel} из раздела \"Деньги\" ({sourceLabel}) на сумму {appliedAmount.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"))}.";
        payment.SystemNotes = string.IsNullOrWhiteSpace(payment.SystemNotes)
            ? note
            : $"{payment.SystemNotes.Trim()}\n{note}";

        if (targetPaymentType == PaymentType.Income && overpaymentAmount > 0.009m && payment.ClientId.HasValue && operation.Id > 0)
        {
            var externalKey = $"money-operation-overpayment:{operation.Id}";
            var existing = await _db.ClientBalanceTransactions
                .FirstOrDefaultAsync(item => item.ExternalKey == externalKey, ct);
            var description = $"Переплата по денежной операции #{operation.Id}";

            if (existing is null)
            {
                _db.ClientBalanceTransactions.Add(new ClientBalanceTransaction
                {
                    ClientId = payment.ClientId.Value,
                    InvoiceId = payment.InvoiceId,
                    PaymentId = payment.Id,
                    Amount = overpaymentAmount,
                    Type = ClientBalanceTransactionType.Overpayment,
                    Direction = ClientBalanceDirection.Credit,
                    Description = description,
                    ExternalKey = externalKey,
                    CreatedAt = DateTime.UtcNow,
                });
            }
            else
            {
                existing.ClientId = payment.ClientId.Value;
                existing.InvoiceId = payment.InvoiceId;
                existing.PaymentId = payment.Id;
                existing.Amount = overpaymentAmount;
                existing.Type = ClientBalanceTransactionType.Overpayment;
                existing.Direction = ClientBalanceDirection.Credit;
                existing.Description = description;
            }

            var overpaymentNote = $"Системно: {DateTime.UtcNow:dd.MM.yyyy HH:mm:ss} — переплата {overpaymentAmount.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"))} зачислена на баланс клиента.";
            payment.SystemNotes = $"{payment.SystemNotes.Trim()}\n{overpaymentNote}";
        }

        await InvoicePaymentStateService.RefreshAfterPaymentChangeAsync(_db, payment, DateTime.UtcNow, ct);
    }

    private static bool NamesLookRelated(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        var normalizedLeft = NormalizeNameForMatch(left);
        var normalizedRight = NormalizeNameForMatch(right);
        if (normalizedLeft.Length < 4 || normalizedRight.Length < 4)
        {
            return false;
        }

        return normalizedLeft.Contains(normalizedRight, StringComparison.OrdinalIgnoreCase) ||
               normalizedRight.Contains(normalizedLeft, StringComparison.OrdinalIgnoreCase) ||
               normalizedLeft.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                   .Intersect(normalizedRight.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase)
                   .Any(part => part.Length >= 4);
    }

    private static string NormalizeNameForMatch(string value)
    {
        var normalized = value.Trim().ToLowerInvariant().Replace('ё', 'е');
        normalized = Regex.Replace(normalized, @"[^\p{L}\p{Nd}]+", " ", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        return Regex.Replace(normalized, @"\s+", " ", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)).Trim();
    }

    private static string? ResolveInvoiceCandidateCounterpartyName(Payment payment)
        => payment.Type == PaymentType.Expense
            ? FirstNonEmpty(
                payment.ExpenseClient?.Name,
                payment.ExpenseClient?.Company,
                payment.ExpenseLegalEntity?.ShortName,
                payment.ExpenseLegalEntity?.FullName,
                payment.CounterpartyName,
                payment.Client?.Name,
                payment.Client?.Company)
            : FirstNonEmpty(
                payment.Client?.Name,
                payment.Client?.Company,
                payment.CounterpartyName,
                payment.ExpenseClient?.Name,
                payment.ExpenseClient?.Company,
                payment.ExpenseLegalEntity?.ShortName,
                payment.ExpenseLegalEntity?.FullName);

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private sealed record BankStatementInvoiceCandidate(
        int PaymentId,
        int InvoiceId,
        string InvoiceNumber,
        string? Account,
        decimal Amount,
        decimal PaidAmount,
        PaymentStatus Status,
        PaymentType PaymentType,
        int? ClientId,
        string? ClientName,
        string? CounterpartyName,
        HashSet<string> InvoiceNumberTokens,
        HashSet<string> ContractNumberTokens)
    {
        public IEnumerable<string> MatchingNumberTokens => InvoiceNumberTokens.Concat(ContractNumberTokens);

        public decimal OutstandingAmount => CalculateOutstandingAmount(Amount, PaidAmount, Status);
    }

    private sealed record BankStatementIncomeTypeCandidate(
        int Id,
        string Name,
        PaymentType PaymentType);

    private sealed record BankStatementCategoryRule(
        string CategoryName,
        PaymentType PaymentType,
        IReadOnlyCollection<string> Keywords,
        int? IncomeTypeId = null,
        bool IsManual = false);

    private sealed class BankStatementAutoImportSettings
    {
        public bool Enabled { get; set; }
        public string? FolderPath { get; set; }
        public string FileMask { get; set; } = "*.csv;*.txt;*.1c";
        public DateTime? LastCheckedAt { get; set; }
    }

    private sealed class BankStatementManualCategoryRulesSettings
    {
        public List<BankStatementManualCategoryRule> Rules { get; set; } = [];
    }

    private sealed class BankStatementManualCategoryRule
    {
        public bool Enabled { get; set; } = true;
        public string CategoryName { get; set; } = string.Empty;
        public PaymentType PaymentType { get; set; } = PaymentType.Income;
        public string Keywords { get; set; } = string.Empty;
        public int? IncomeTypeId { get; set; }
    }

    private sealed record InvoiceLinkValidationResult(Invoice? Invoice, string? Error);

    public sealed class LinkMoneyOperationInvoiceRequest
    {
        public int InvoiceId { get; init; }
        public string? Comment { get; init; }
    }

    private static bool TryParseStatementDate(string value, out DateTime date)
    {
        var formats = new[] { "dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy" };
        return DateTime.TryParseExact(value.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date) ||
               DateTime.TryParse(value.Trim(), CultureInfo.GetCultureInfo("ru-RU"), DateTimeStyles.None, out date);
    }

    private static bool TryParseStatementAmount(string value, out decimal amount)
    {
        var normalized = value
            .Trim()
            .Replace("\u00A0", string.Empty)
            .Replace(" ", string.Empty)
            .Replace("₽", string.Empty)
            .Replace("руб.", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("руб", string.Empty, StringComparison.OrdinalIgnoreCase);

        if (normalized.Contains(',') && normalized.Contains('.'))
        {
            normalized = normalized.Replace(".", string.Empty).Replace(',', '.');
        }
        else
        {
            normalized = normalized.Replace(',', '.');
        }

        return decimal.TryParse(normalized, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out amount);
    }

    private sealed class MoneyOperationQueryRow
    {
        public int Id { get; init; }
        public string Source { get; init; } = string.Empty;
        public string Direction { get; init; } = string.Empty;
        public string AccountingType { get; init; } = string.Empty;
        public int? MoneyAccountId { get; init; }
        public string? MoneyAccountName { get; init; }
        public string? AccountName { get; init; }
        public string? CounterpartyName { get; init; }
        public int? ResponsibleUserId { get; init; }
        public string? ResponsibleName { get; init; }
        public int? IncomeTypeId { get; init; }
        public string? CategoryName { get; init; }
        public decimal Amount { get; init; }
        public decimal? VatRate { get; init; }
        public decimal? VatAmount { get; init; }
        public string? Currency { get; init; }
        public string? PaymentOrderNumber { get; init; }
        public DateTime Date { get; init; }
        public DateTime? RecognitionDate { get; init; }
        public bool IsAdvancePayment { get; init; }
        public string? Purpose { get; init; }
        public string? PaidInvoiceNumber { get; init; }
        public decimal? PaidAmount { get; init; }
        public string? ProjectName { get; init; }
        public string? DirectionName { get; init; }
        public string? SalesPointName { get; init; }
        public int? PaymentPriority { get; init; }
        public string? PaidAccountName { get; init; }
        public int? LinkedInvoiceId { get; init; }
        public string? LinkedInvoiceNumber { get; init; }
        public DateTime? LinkedInvoiceDate { get; init; }
        public int? LinkedContractId { get; init; }
        public string? LinkedContractNumber { get; init; }
        public int? LinkedActId { get; init; }
        public string? LinkedActNumber { get; init; }
        public DateTime? LinkedActDate { get; init; }
        public string? MatchStatus { get; init; }
        public DateTime? MatchedAt { get; init; }
        public string? MatchComment { get; init; }
        public int? AttachmentCount { get; init; }
        public string? AttachmentSummary { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime? UpdatedAt { get; init; }
        public bool IsReadonly { get; init; }
        public int? RelatedPaymentId { get; init; }
        public int? CashOrderId { get; init; }
        public string? CashOrderNumber { get; init; }
        public DateTime? CashOrderDate { get; init; }
        public string? CashOrderStatus { get; init; }
        public bool CanCreateCashOrder { get; init; }
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

    private sealed class MoneyOperationDailyBalanceOpeningQueryRow
    {
        public string Source { get; init; } = string.Empty;
        public string AccountName { get; init; } = string.Empty;
        public decimal Balance { get; init; }
    }

    private sealed class MoneyOperationDailyBalanceQueryRow
    {
        public DateTime Date { get; init; }
        public string Source { get; init; } = string.Empty;
        public string AccountName { get; init; } = string.Empty;
        public decimal Income { get; init; }
        public decimal Expense { get; init; }
        public int IncomeCount { get; init; }
        public int ExpenseCount { get; init; }
    }

    private sealed class StatementHeaderMap
    {
        public int Date { get; set; } = -1;
        public int Amount { get; set; } = -1;
        public int Income { get; set; } = -1;
        public int Expense { get; set; } = -1;
        public int Counterparty { get; set; } = -1;
        public int Purpose { get; set; } = -1;
        public int DocumentNumber { get; set; } = -1;
        public int Account { get; set; } = -1;
    }

    private sealed record BankStatementImportItem(
        int RowNumber,
        DateTime Date,
        MoneyOperationDirection Direction,
        decimal Amount,
        string AccountName,
        string? CounterpartyName,
        string? Purpose,
        string? DocumentNumber);
}
