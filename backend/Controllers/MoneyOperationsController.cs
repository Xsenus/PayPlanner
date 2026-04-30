using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Helpers;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/money-operations")]
[Authorize]
public class MoneyOperationsController : ControllerBase
{
    private const long MaxStatementFileSizeBytes = 10L * 1024L * 1024L;
    private const string CashSourceToken = "\u043A\u0430\u0441";
    private const string CashSourceTitleToken = "\u041A\u0430\u0441";
    private const string CashSourceAlternativeToken = "\u043D\u0430\u043B\u0438\u0447";
    private const string CashSourceAlternativeTitleToken = "\u041D\u0430\u043B\u0438\u0447";
    private const string CardSourceToken = "\u043A\u0430\u0440\u0442";
    private const string CardSourceTitleToken = "\u041A\u0430\u0440\u0442";

    private readonly PaymentContext _db;
    private readonly ILogger<MoneyOperationsController> _logger;

    public MoneyOperationsController(PaymentContext db, ILogger<MoneyOperationsController> logger)
    {
        _db = db;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] MoneyOperationSource? source,
        [FromQuery] MoneyOperationDirection? direction,
        [FromQuery] MoneyOperationAccountingType? accountingType,
        [FromQuery] string? accountName = null,
        [FromQuery] int? linkedInvoiceId = null,
        [FromQuery] string? search = null,
        CancellationToken ct = default)
    {
        var list = await SqliteRetryHelper.ExecuteAsync(
            async cancellationToken =>
            {
                var rows = await BuildMoneyOperationsQuery(from, to, source, direction, accountingType, accountName, search)
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
        [FromQuery] string? accountName = null,
        [FromQuery] int? linkedInvoiceId = null,
        [FromQuery] string? search = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 5, 200);

        var response = await SqliteRetryHelper.ExecuteAsync(async cancellationToken =>
        {
            var query = BuildMoneyOperationsQuery(from, to, source, direction, accountingType, accountName, search)
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
                ? BuildMoneyOperationsQuery(null, from.Value.Date.AddDays(-1), source, direction, MoneyOperationAccountingType.Fact, accountName, search)
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

    [HttpGet("accounts")]
    public async Task<ActionResult<IReadOnlyList<string>>> GetAccounts(
        [FromQuery] MoneyOperationSource? source,
        CancellationToken ct = default)
    {
        var accounts = await SqliteRetryHelper.ExecuteAsync(
            async cancellationToken => await BuildMoneyOperationsQuery(null, null, source, null, null, null, null)
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
        var summaries = await SqliteRetryHelper.ExecuteAsync(async cancellationToken =>
        {
            var openingQuery = from.HasValue
                ? BuildMoneyOperationsQuery(null, from.Value.Date.AddDays(-1), source, null, MoneyOperationAccountingType.Fact, null, null)
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

            var periodRows = await BuildMoneyOperationsQuery(from, to, source, null, MoneyOperationAccountingType.Fact, null, null)
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

    private IQueryable<MoneyOperationQueryRow> BuildMoneyOperationsQuery(
        DateTime? from,
        DateTime? to,
        MoneyOperationSource? source,
        MoneyOperationDirection? direction,
        MoneyOperationAccountingType? accountingType,
        string? accountName,
        string? search)
    {
        var customOperations = ApplyMoneyOperationFilters(
                _db.MoneyOperations.AsNoTracking(),
                from,
                to,
                source,
                direction,
                accountingType,
                accountName,
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
                AccountName = operation.AccountName,
                CounterpartyName = operation.CounterpartyName,
                CategoryName = operation.CategoryName,
                Amount = operation.Amount,
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
                MatchStatus = operation.MatchStatus == MoneyOperationMatchStatus.Matched
                    ? nameof(MoneyOperationMatchStatus.Matched)
                    : operation.MatchStatus == MoneyOperationMatchStatus.ClarificationRequired
                        ? nameof(MoneyOperationMatchStatus.ClarificationRequired)
                        : operation.MatchStatus == MoneyOperationMatchStatus.Ignored
                            ? nameof(MoneyOperationMatchStatus.Ignored)
                            : nameof(MoneyOperationMatchStatus.None),
                MatchedAt = operation.MatchedAt,
                MatchComment = operation.MatchComment,
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
                accountName,
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
                            : nameof(MoneyOperationSource.Bank),
                Direction = payment.Type == PaymentType.Income
                    ? nameof(MoneyOperationDirection.Income)
                    : nameof(MoneyOperationDirection.Expense),
                AccountingType = payment.Status == PaymentStatus.Completed
                    ? nameof(MoneyOperationAccountingType.Fact)
                    : nameof(MoneyOperationAccountingType.Plan),
                AccountName = payment.Account,
                CounterpartyName = payment.CounterpartyName,
                CategoryName = payment.ClientCase != null ? payment.ClientCase.Title : null,
                Amount =
                    payment.Status == PaymentStatus.Completed && payment.PaidAmount > 0m
                        ? payment.PaidAmount
                        : payment.Amount,
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
                MatchStatus = payment.InvoiceId.HasValue
                    ? nameof(MoneyOperationMatchStatus.Matched)
                    : nameof(MoneyOperationMatchStatus.None),
                MatchedAt = payment.LastPaymentDate ?? payment.PaidDate,
                MatchComment = null,
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
        string? accountName,
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

        if (!string.IsNullOrWhiteSpace(accountName))
        {
            var accountTerm = accountName.Trim();
            query = query.Where(operation => operation.AccountName == accountTerm);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var titleTerm = ToTitleSearchTerm(term);
            query = query.Where(operation =>
                (operation.CounterpartyName ?? string.Empty).Contains(term) ||
                (operation.Purpose ?? string.Empty).Contains(term) ||
                (operation.CategoryName ?? string.Empty).Contains(term) ||
                (titleTerm != term &&
                    ((operation.CounterpartyName ?? string.Empty).Contains(titleTerm) ||
                     (operation.Purpose ?? string.Empty).Contains(titleTerm) ||
                     (operation.CategoryName ?? string.Empty).Contains(titleTerm))));
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
        string? accountName,
        string? search)
    {
        query = query.Where(payment => payment.Status == PaymentStatus.Completed);

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
                MoneyOperationSource.Bank => query.Where(payment =>
                    payment.PaymentSource == null ||
                    payment.PaymentSource.Name == null ||
                    (!payment.PaymentSource.Name.Contains(CashSourceToken) &&
                     !payment.PaymentSource.Name.Contains(CashSourceTitleToken) &&
                     !payment.PaymentSource.Name.Contains(CashSourceAlternativeToken) &&
                     !payment.PaymentSource.Name.Contains(CashSourceAlternativeTitleToken) &&
                     !payment.PaymentSource.Name.Contains(CardSourceToken) &&
                     !payment.PaymentSource.Name.Contains(CardSourceTitleToken))),
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

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var titleTerm = ToTitleSearchTerm(term);
            query = query.Where(payment =>
                (payment.CounterpartyName ?? string.Empty).Contains(term) ||
                (payment.Description ?? string.Empty).Contains(term) ||
                (payment.Account ?? string.Empty).Contains(term) ||
                (titleTerm != term &&
                    ((payment.CounterpartyName ?? string.Empty).Contains(titleTerm) ||
                     (payment.Description ?? string.Empty).Contains(titleTerm) ||
                     (payment.Account ?? string.Empty).Contains(titleTerm))));
        }

        return query;
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id, CancellationToken ct)
    {
        var item = await _db.MoneyOperations.AsNoTracking().FirstOrDefaultAsync(operation => operation.Id == id, ct);
        return item is null ? NotFound() : Ok(item);
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

        if (operation.Direction != MoneyOperationDirection.Income)
        {
            return Ok(Array.Empty<MoneyOperationInvoiceCandidateDto>());
        }

        var purposeTokens = ExtractInvoiceNumberTokens(operation.Purpose)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var operationAmount = Math.Round(operation.Amount, 2, MidpointRounding.AwayFromZero);
        var operationCounterparty = operation.CounterpartyName?.Trim();

        var invoicePayments = await _db.Payments
            .AsNoTracking()
            .Include(payment => payment.Invoice)
            .Include(payment => payment.Client)
            .Where(payment =>
                payment.DocumentKind == DocumentKind.Invoice &&
                payment.Type == PaymentType.Income &&
                payment.Status != PaymentStatus.Cancelled &&
                payment.InvoiceId.HasValue)
            .ToListAsync(ct);

        var candidates = invoicePayments
            .Select(payment =>
            {
                var invoiceNumber = payment.Invoice?.Number ?? payment.Account ?? payment.LegacyAccountNumber ?? string.Empty;
                var numberTokens = BuildInvoiceNumberTokenSet(payment.Invoice?.Number, payment.Account, payment.LegacyAccountNumber);
                var outstandingAmount = CalculateOutstandingAmount(payment.Amount, payment.PaidAmount, payment.Status);
                var score = 0;
                var reasons = new List<string>();

                if (purposeTokens.Count > 0 && numberTokens.Any(purposeTokens.Contains))
                {
                    score += 100;
                    reasons.Add("номер счёта найден в назначении");
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

                var candidateName = payment.Client?.Name ?? payment.CounterpartyName;
                if (NamesLookRelated(operationCounterparty, candidateName))
                {
                    score += 20;
                    reasons.Add("похожий контрагент");
                }

                return new MoneyOperationInvoiceCandidateDto
                {
                    InvoiceId = payment.InvoiceId!.Value,
                    PaymentId = payment.Id,
                    InvoiceNumber = invoiceNumber,
                    InvoiceDate = payment.Invoice?.Date ?? payment.AccountDate,
                    Amount = payment.Amount,
                    PaidAmount = payment.PaidAmount,
                    OutstandingAmount = outstandingAmount,
                    Status = payment.Status,
                    ClientId = payment.ClientId,
                    ClientName = payment.Client?.Name,
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
    public async Task<IActionResult> Create([FromBody] MoneyOperation model)
    {
        if (model.Amount < 0)
        {
            return BadRequest("Amount must be non-negative.");
        }

        model.AccountName = string.IsNullOrWhiteSpace(model.AccountName) ? null : model.AccountName.Trim();
        model.CounterpartyName = string.IsNullOrWhiteSpace(model.CounterpartyName) ? null : model.CounterpartyName.Trim();
        model.CategoryName = string.IsNullOrWhiteSpace(model.CategoryName) ? null : model.CategoryName.Trim();
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
            var validation = await ValidateInvoiceLinkAsync(model, model.LinkedInvoiceId.Value);
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
        await _db.SaveChangesAsync();
        if (model.LinkedInvoiceId.HasValue && model.MatchStatus == MoneyOperationMatchStatus.Matched)
        {
            await ApplyMoneyOperationToInvoicePaymentAsync(model, model.LinkedInvoiceId.Value);
            await _db.SaveChangesAsync();
        }
        await SyncCashOrderAsync(model);
        return Created($"/api/money-operations/{model.Id}", model);
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

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (extension is not ".csv" and not ".txt" and not ".tsv")
        {
            return BadRequest("Поддерживаются файлы CSV, TSV и TXT.");
        }

        await using var stream = file.OpenReadStream();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, ct);
        var statementBytes = memory.ToArray();
        var fileHash = Convert.ToHexString(SHA256.HashData(statementBytes)).ToLowerInvariant();
        var text = DecodeStatementBytes(statementBytes);
        if (LooksLikeClientBankExchange(text))
        {
            var clientBankResult = ParseClientBankExchange(text);
            return Ok(await ImportBankStatementItemsAsync(
                clientBankResult.Items,
                clientBankResult.Result,
                file.FileName,
                statementBytes.Length,
                fileHash,
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

        return Ok(await ImportBankStatementItemsAsync(importItems, result, file.FileName, statementBytes.Length, fileHash, ct));
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

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] MoneyOperation model)
    {
        var entity = await _db.MoneyOperations.FindAsync(id);
        if (entity is null)
        {
            return NotFound();
        }

        if (model.Amount < 0)
        {
            return BadRequest("Amount must be non-negative.");
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

        entity.Source = model.Source;
        entity.Direction = model.Direction;
        entity.AccountingType = model.AccountingType;
        entity.Amount = model.Amount;
        entity.Currency = string.IsNullOrWhiteSpace(model.Currency) ? null : model.Currency.Trim();
        entity.AccountName = string.IsNullOrWhiteSpace(model.AccountName) ? null : model.AccountName.Trim();
        entity.CounterpartyName = string.IsNullOrWhiteSpace(model.CounterpartyName) ? null : model.CounterpartyName.Trim();
        entity.CategoryName = string.IsNullOrWhiteSpace(model.CategoryName) ? null : model.CategoryName.Trim();
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

        await _db.SaveChangesAsync();
        await SyncCashOrderAsync(entity);
        return Ok(entity);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var entity = await _db.MoneyOperations.FindAsync(id);
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
            .ToListAsync();
        foreach (var order in linkedOrders)
        {
            order.Status = CashOrderStatus.Cancelled;
            order.UpdatedAt = DateTime.UtcNow;
        }

        _db.MoneyOperations.Remove(entity);
        await _db.SaveChangesAsync();
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

    private static string ToTitleSearchTerm(string value)
        => string.IsNullOrWhiteSpace(value)
            ? value
            : string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Length == 1
                    ? part.ToUpperInvariant()
                    : char.ToUpperInvariant(part[0]) + part[1..]));

    private static MoneyOperationListItemDto MapMoneyOperationRow(MoneyOperationQueryRow row)
        => new()
        {
            Id = row.Id,
            Source = ParseEnum<MoneyOperationSource>(row.Source),
            Direction = ParseEnum<MoneyOperationDirection>(row.Direction),
            AccountingType = ParseEnum<MoneyOperationAccountingType>(row.AccountingType),
            AccountName = row.AccountName,
            CounterpartyName = row.CounterpartyName,
            CategoryName = row.CategoryName,
            Amount = row.Amount,
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
            MatchStatus = string.IsNullOrWhiteSpace(row.MatchStatus)
                ? MoneyOperationMatchStatus.None
                : ParseEnum<MoneyOperationMatchStatus>(row.MatchStatus),
            MatchedAt = row.MatchedAt,
            MatchComment = row.MatchComment,
            CreatedAt = row.CreatedAt,
            UpdatedAt = row.UpdatedAt,
            IsReadonly = row.IsReadonly,
            RelatedPaymentId = row.RelatedPaymentId,
            CashOrderId = row.CashOrderId,
            CashOrderNumber = row.CashOrderNumber,
            CashOrderStatus = string.IsNullOrWhiteSpace(row.CashOrderStatus)
                ? null
                : ParseEnum<CashOrderStatus>(row.CashOrderStatus),
            CanCreateCashOrder = row.CanCreateCashOrder,
        };

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
            !sourceName.Contains(CardSourceTitleToken));

    private async Task<BankStatementImportResultDto> ImportBankStatementItemsAsync(
        IReadOnlyCollection<BankStatementImportItem> items,
        BankStatementImportResultDto result,
        string fileName,
        long fileSize,
        string fileHash,
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
        var invoiceNumbersFromStatement = items
            .SelectMany(item => ExtractInvoiceNumberTokens(item.Purpose))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var invoiceCandidates = (await _db.Payments
                .AsNoTracking()
                .Include(payment => payment.Invoice)
                .Include(payment => payment.Client)
                .Where(payment =>
                    payment.DocumentKind == DocumentKind.Invoice &&
                    payment.Type == PaymentType.Income &&
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
                    payment.ClientId,
                    payment.Client != null ? payment.Client.Name : null,
                    payment.CounterpartyName,
                    BuildInvoiceNumberTokenSet(payment.Invoice?.Number, payment.Account, payment.LegacyAccountNumber)))
                .Where(candidate =>
                    invoiceNumbersFromStatement.Count == 0 ||
                    candidate.NumberTokens.Any(invoiceNumbersFromStatement.Contains) ||
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

            var operation = new MoneyOperation
            {
                Source = MoneyOperationSource.Bank,
                Direction = item.Direction,
                AccountingType = MoneyOperationAccountingType.Fact,
                AccountName = item.AccountName,
                CounterpartyName = item.CounterpartyName,
                CategoryName = item.Direction == MoneyOperationDirection.Income ? "Поступление по выписке" : "Списание по выписке",
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
        if (item.Direction != MoneyOperationDirection.Income)
        {
            return null;
        }

        var tokens = ExtractInvoiceNumberTokens(item.Purpose).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (tokens.Count > 0)
        {
            var matchingCandidates = candidates
                .Where(candidate =>
                    candidate.OutstandingAmount > 0m &&
                    item.Amount <= candidate.OutstandingAmount + 0.009m &&
                    candidate.NumberTokens.Any(tokens.Contains))
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
        if (tokens.Count > 0 && candidate.NumberTokens.Any(tokens.Contains))
        {
            return $"Автоматически сверено по номеру счёта {candidate.InvoiceNumber} из назначения платежа.";
        }

        return $"Автоматически сверено со счётом {candidate.InvoiceNumber} по сумме и клиенту из выписки.";
    }

    private static IEnumerable<string> ExtractInvoiceNumberTokens(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        foreach (Match match in Regex.Matches(
                     text,
                     @"(?:сч[её]т\w*|invoice|inv)\s*(?:№|#|n|no\.?)?\s*([A-Za-zА-Яа-я0-9][A-Za-zА-Яа-я0-9\/\-_]*)",
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

        var invoicePayment = await _db.Payments
            .AsNoTracking()
            .Where(payment =>
                payment.InvoiceId == invoiceId &&
                payment.DocumentKind == DocumentKind.Invoice &&
                payment.Type == PaymentType.Income &&
                payment.Status != PaymentStatus.Cancelled)
            .OrderByDescending(payment => payment.Id)
            .FirstOrDefaultAsync(ct);
        if (invoicePayment is null)
        {
            return new(invoice, null);
        }

        var outstandingAmount = CalculateOutstandingAmount(invoicePayment.Amount, invoicePayment.PaidAmount, invoicePayment.Status);
        if (outstandingAmount <= 0m)
        {
            return new(invoice, "Счёт уже полностью оплачен, привязка денежной операции не выполнена.");
        }

        if (operation.Amount > outstandingAmount + 0.009m && !invoicePayment.ClientId.HasValue)
        {
            return new(invoice, $"Сумма денежной операции больше остатка по счёту, а клиент счёта не указан для зачисления переплаты. Остаток: {outstandingAmount:0.00}.");
        }

        return new(invoice, null);
    }

    private async Task ApplyMoneyOperationToInvoicePaymentAsync(
        MoneyOperation operation,
        int invoiceId,
        CancellationToken ct = default)
    {
        if (operation.Direction != MoneyOperationDirection.Income || operation.Amount <= 0m)
        {
            return;
        }

        var payment = await _db.Payments
            .Where(item =>
                item.InvoiceId == invoiceId &&
                item.DocumentKind == DocumentKind.Invoice &&
                item.Type == PaymentType.Income &&
                item.Status != PaymentStatus.Cancelled)
            .OrderByDescending(item => item.Id)
            .FirstOrDefaultAsync(ct);
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
        var note = $"Системно: {DateTime.UtcNow:dd.MM.yyyy HH:mm:ss} — зачтено поступление из раздела \"Деньги\" ({sourceLabel}) на сумму {appliedAmount.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"))}.";
        payment.SystemNotes = string.IsNullOrWhiteSpace(payment.SystemNotes)
            ? note
            : $"{payment.SystemNotes.Trim()}\n{note}";

        if (overpaymentAmount > 0.009m && payment.ClientId.HasValue && operation.Id > 0)
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
        => Regex.Replace(value.Trim().ToLowerInvariant(), @"\s+", " ", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private sealed record BankStatementInvoiceCandidate(
        int PaymentId,
        int InvoiceId,
        string InvoiceNumber,
        string? Account,
        decimal Amount,
        decimal PaidAmount,
        PaymentStatus Status,
        int? ClientId,
        string? ClientName,
        string? CounterpartyName,
        HashSet<string> NumberTokens)
    {
        public decimal OutstandingAmount => CalculateOutstandingAmount(Amount, PaidAmount, Status);
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
        public string? AccountName { get; init; }
        public string? CounterpartyName { get; init; }
        public string? CategoryName { get; init; }
        public decimal Amount { get; init; }
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
        public string? MatchStatus { get; init; }
        public DateTime? MatchedAt { get; init; }
        public string? MatchComment { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime? UpdatedAt { get; init; }
        public bool IsReadonly { get; init; }
        public int? RelatedPaymentId { get; init; }
        public int? CashOrderId { get; init; }
        public string? CashOrderNumber { get; init; }
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
