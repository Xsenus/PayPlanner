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
