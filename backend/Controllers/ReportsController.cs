using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Helpers;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/reports")]
[Authorize]
public class ReportsController : ControllerBase
{
    private readonly PaymentContext _db;
    private readonly ILogger<ReportsController> _logger;

    public ReportsController(PaymentContext db, ILogger<ReportsController> logger)
    {
        _db = db;
        _logger = logger;
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

        var report = await SqliteRetryHelper.ExecuteAsync(
            cancellationToken => BuildCashFlowReportAsync(start, end, source, normalizedAccountName, cancellationToken),
            _logger,
            ct);

        return Ok(report);
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

        var report = await SqliteRetryHelper.ExecuteAsync(
            cancellationToken => BuildCategoryReportAsync(start, end, source, cancellationToken),
            _logger,
            ct);

        return Ok(report);
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

        var report = await SqliteRetryHelper.ExecuteAsync(
            cancellationToken => BuildCounterpartyReportAsync(start, end, source, cancellationToken),
            _logger,
            ct);

        return Ok(report);
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

        var report = await SqliteRetryHelper.ExecuteAsync(
            cancellationToken => BuildProjectReportAsync(start, end, source, cancellationToken),
            _logger,
            ct);

        return Ok(report);
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

        var report = await SqliteRetryHelper.ExecuteAsync(
            cancellationToken => BuildDirectionReportAsync(start, end, source, cancellationToken),
            _logger,
            ct);

        return Ok(report);
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

        var report = await SqliteRetryHelper.ExecuteAsync(
            cancellationToken => BuildSalesPointReportAsync(start, end, source, cancellationToken),
            _logger,
            ct);

        return Ok(report);
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

        var report = await SqliteRetryHelper.ExecuteAsync(
            cancellationToken => BuildVatReportAsync(start, end, source, cancellationToken),
            _logger,
            ct);

        return Ok(report);
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

        var report = await SqliteRetryHelper.ExecuteAsync(
            cancellationToken => BuildPlanFactReportAsync(start, end, source, cancellationToken),
            _logger,
            ct);

        return Ok(report);
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

        var report = await SqliteRetryHelper.ExecuteAsync(
            cancellationToken => BuildAdvanceReportAsync(start, end, source, cancellationToken),
            _logger,
            ct);

        return Ok(report);
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

        var report = await SqliteRetryHelper.ExecuteAsync(
            cancellationToken => BuildPaymentPriorityReportAsync(start, end, source, cancellationToken),
            _logger,
            ct);

        return Ok(report);
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

        var report = await SqliteRetryHelper.ExecuteAsync(
            cancellationToken => BuildPaidAccountReportAsync(start, end, source, cancellationToken),
            _logger,
            ct);

        return Ok(report);
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

        var report = await SqliteRetryHelper.ExecuteAsync(
            cancellationToken => BuildPaidInvoiceReportAsync(start, end, source, cancellationToken),
            _logger,
            ct);

        return Ok(report);
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

        var report = await SqliteRetryHelper.ExecuteAsync(
            cancellationToken => BuildInvoiceStatusReportAsync(start, end, cancellationToken),
            _logger,
            ct);

        return Ok(report);
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

        var report = await SqliteRetryHelper.ExecuteAsync(
            cancellationToken => BuildActStatusReportAsync(start, end, cancellationToken),
            _logger,
            ct);

        return Ok(report);
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

        var report = await SqliteRetryHelper.ExecuteAsync(
            cancellationToken => BuildContractStatusReportAsync(start, end, cancellationToken),
            _logger,
            ct);

        return Ok(report);
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

        var report = await SqliteRetryHelper.ExecuteAsync(
            cancellationToken => BuildPaymentOrderReportAsync(start, end, source, cancellationToken),
            _logger,
            ct);

        return Ok(report);
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

        var report = await SqliteRetryHelper.ExecuteAsync(
            cancellationToken => BuildPaymentPurposeReportAsync(start, end, source, cancellationToken),
            _logger,
            ct);

        return Ok(report);
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

        var report = await SqliteRetryHelper.ExecuteAsync(
            cancellationToken => BuildMatchStatusReportAsync(start, end, source, cancellationToken),
            _logger,
            ct);

        return Ok(report);
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

        var report = await SqliteRetryHelper.ExecuteAsync(
            cancellationToken => BuildJournalStatusReportAsync(start, end, source, cancellationToken),
            _logger,
            ct);

        return Ok(report);
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

        var report = await SqliteRetryHelper.ExecuteAsync(
            cancellationToken => BuildCurrencyReportAsync(start, end, source, cancellationToken),
            _logger,
            ct);

        return Ok(report);
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

        var report = await SqliteRetryHelper.ExecuteAsync(
            cancellationToken => BuildSpreadsheetImportReportAsync(start, end, cancellationToken),
            _logger,
            ct);

        return Ok(report);
    }

    private async Task<CashFlowReportDto> BuildCashFlowReportAsync(
        DateTime from,
        DateTime to,
        MoneyOperationSource? source,
        string? accountName,
        CancellationToken ct)
    {
        var openingRows = await BuildFactQuery(null, from.AddDays(-1), source, accountName)
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

        var periodRows = await BuildFactQuery(from, to, source, accountName)
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

        var dailyRows = await BuildFactQuery(from, to, source, accountName)
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
        string? accountName)
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

        return query;
    }

    private async Task<MoneyCategoryReportDto> BuildCategoryReportAsync(
        DateTime from,
        DateTime to,
        MoneyOperationSource? source,
        CancellationToken ct)
    {
        var query = BuildFactQuery(from, to, source, null)
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
        CancellationToken ct)
    {
        var rows = await BuildFactQuery(from, to, source, null)
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
        CancellationToken ct)
    {
        var rows = await BuildFactQuery(from, to, source, null)
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
        CancellationToken ct)
    {
        var rows = await BuildFactQuery(from, to, source, null)
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
        CancellationToken ct)
    {
        var rows = await BuildFactQuery(from, to, source, null)
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
        CancellationToken ct)
    {
        var rows = await BuildFactQuery(from, to, source, null)
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
        CancellationToken ct)
    {
        var rows = await BuildPlanFactQuery(from, to, source)
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
        MoneyOperationSource? source)
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

        return query;
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
        CancellationToken ct)
    {
        var rows = await BuildFactQuery(from, to, source, null)
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
        CancellationToken ct)
    {
        var rows = await BuildFactQuery(from, to, source, null)
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
        CancellationToken ct)
    {
        var rows = await BuildFactQuery(from, to, source, null)
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
        CancellationToken ct)
    {
        var rows = await BuildFactQuery(from, to, source, null)
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
        CancellationToken ct)
    {
        var rows = await _db.Payments
            .AsNoTracking()
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
            InvoiceWorkflowStatus.Overdue => 0,
            InvoiceWorkflowStatus.OnSigning => 1,
            InvoiceWorkflowStatus.SignedByClient => 2,
            InvoiceWorkflowStatus.PartiallyPaid => 3,
            InvoiceWorkflowStatus.Draft => 4,
            InvoiceWorkflowStatus.Paid => 5,
            InvoiceWorkflowStatus.Cancelled => 6,
            _ => 7,
        };

    private static string FormatInvoiceWorkflowStatusLabel(string status)
        => status switch
        {
            InvoiceWorkflowStatus.Draft => "Черновик",
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
        CancellationToken ct)
    {
        var rows = await _db.Acts
            .AsNoTracking()
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
            ActWorkflowStatus.OnSigning => 0,
            ActWorkflowStatus.Draft => 1,
            ActWorkflowStatus.Signed => 2,
            ActWorkflowStatus.Cancelled => 3,
            _ => 4,
        };

    private static string FormatActWorkflowStatusLabel(string status)
        => status switch
        {
            ActWorkflowStatus.Draft => "Черновик",
            ActWorkflowStatus.OnSigning => "На подписании",
            ActWorkflowStatus.Signed => "Подписан",
            ActWorkflowStatus.Cancelled => "Отменен",
            _ => status,
        };

    private async Task<ContractStatusReportDto> BuildContractStatusReportAsync(
        DateTime from,
        DateTime to,
        CancellationToken ct)
    {
        var rows = await _db.Contracts
            .AsNoTracking()
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

        var activeStatuses = new[] { ContractStatus.Draft, ContractStatus.OnSigning, ContractStatus.Concluded };

        return new ContractStatusReportDto
        {
            From = from,
            To = to,
            ContractCount = normalizedRows.Count,
            DraftCount = normalizedRows.Count(item => item.Status == ContractStatus.Draft),
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
            ContractStatus.OnSigning => 0,
            ContractStatus.Draft => 1,
            ContractStatus.Concluded => 2,
            ContractStatus.Terminated => 3,
            ContractStatus.Cancelled => 4,
            _ => 5,
        };

    private static string FormatContractStatusLabel(string status)
        => status switch
        {
            ContractStatus.Draft => "Черновик",
            ContractStatus.OnSigning => "На подписании",
            ContractStatus.Concluded => "Заключен",
            ContractStatus.Terminated => "Расторгнут",
            ContractStatus.Cancelled => "Аннулирован",
            _ => status,
        };

    private async Task<PaymentOrderReportDto> BuildPaymentOrderReportAsync(
        DateTime from,
        DateTime to,
        MoneyOperationSource? source,
        CancellationToken ct)
    {
        var rows = await BuildFactQuery(from, to, source, null)
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
        CancellationToken ct)
    {
        var rows = await BuildFactQuery(from, to, source, null)
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
        CancellationToken ct)
    {
        var rows = await BuildFactQuery(from, to, source, null)
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
        CancellationToken ct)
    {
        var rows = await BuildFactQuery(from, to, source, null)
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
        CancellationToken ct)
    {
        var rows = await BuildFactQuery(from, to, source, null)
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
