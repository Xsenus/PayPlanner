using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Helpers;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/money-operations")]
[Authorize]
public class MoneyOperationsController : ControllerBase
{
    private const string CashSourceToken = "\u043A\u0430\u0441";
    private const string CardSourceToken = "\u043A\u0430\u0440\u0442";

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
        [FromQuery] string? search,
        CancellationToken ct)
    {
        var list = await SqliteRetryHelper.ExecuteAsync(
            async cancellationToken => await BuildMoneyOperationsQuery(from, to, source, direction, accountingType, search)
                .OrderByDescending(item => item.Date)
                .ThenByDescending(item => item.Id)
                .ToListAsync(cancellationToken),
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
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 5, 200);

        var response = await SqliteRetryHelper.ExecuteAsync(async cancellationToken =>
        {
            var query = BuildMoneyOperationsQuery(from, to, source, direction, accountingType, search);
            var total = await query.CountAsync(cancellationToken);
            var items = await query
                .OrderByDescending(item => item.Date)
                .ThenByDescending(item => item.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(cancellationToken);

            var factSummary = await query
                .Where(item => item.AccountingType == MoneyOperationAccountingType.Fact)
                .GroupBy(_ => 1)
                .Select(group => new
                {
                    Income = group
                        .Where(item => item.Direction == MoneyOperationDirection.Income)
                        .Sum(item => item.Amount),
                    Expense = group
                        .Where(item => item.Direction == MoneyOperationDirection.Expense)
                        .Sum(item => item.Amount),
                    IncomeCount = group.Count(item => item.Direction == MoneyOperationDirection.Income),
                    ExpenseCount = group.Count(item => item.Direction == MoneyOperationDirection.Expense),
                })
                .FirstOrDefaultAsync(cancellationToken);

            var sourceRows = await query
                .GroupBy(item => item.Source)
                .Select(group => new MoneyOperationSourceSummaryDto
                {
                    Source = group.Key,
                    StartBalance = 0m,
                    Income = group
                        .Where(item => item.Direction == MoneyOperationDirection.Income)
                        .Sum(item => item.Amount),
                    Expense = group
                        .Where(item => item.Direction == MoneyOperationDirection.Expense)
                        .Sum(item => item.Amount),
                    EndBalance =
                        group.Where(item => item.Direction == MoneyOperationDirection.Income).Sum(item => item.Amount) -
                        group.Where(item => item.Direction == MoneyOperationDirection.Expense).Sum(item => item.Amount),
                })
                .ToListAsync(cancellationToken);

            var orderedSources = new[]
            {
                MoneyOperationSource.Cash,
                MoneyOperationSource.Bank,
                MoneyOperationSource.Card,
            };

            var orderedSourceRows = orderedSources
                .Select(sourceKey => sourceRows.FirstOrDefault(row => row.Source == sourceKey) ?? new MoneyOperationSourceSummaryDto
                {
                    Source = sourceKey,
                    StartBalance = 0m,
                    Income = 0m,
                    Expense = 0m,
                    EndBalance = 0m,
                })
                .ToList();

            var income = factSummary?.Income ?? 0m;
            var expense = factSummary?.Expense ?? 0m;

            return new MoneyOperationsPageResponse
            {
                Items = items,
                Total = total,
                Page = page,
                PageSize = pageSize,
                Summary = new MoneyOperationsSummaryDto
                {
                    StartBalance = 0m,
                    Income = income,
                    Expense = expense,
                    EndBalance = income - expense,
                    IncomeCount = factSummary?.IncomeCount ?? 0,
                    ExpenseCount = factSummary?.ExpenseCount ?? 0,
                    BySource = orderedSourceRows,
                },
            };
        }, _logger, ct);

        return Ok(response);
    }

    private IQueryable<MoneyOperationListItemDto> BuildMoneyOperationsQuery(
        DateTime? from,
        DateTime? to,
        MoneyOperationSource? source,
        MoneyOperationDirection? direction,
        MoneyOperationAccountingType? accountingType,
        string? search)
    {
        var customOperations = ApplyMoneyOperationFilters(
                _db.MoneyOperations.AsNoTracking(),
                from,
                to,
                source,
                direction,
                accountingType,
                search)
            .Select(operation => new MoneyOperationListItemDto
            {
                Id = operation.Id,
                Source = operation.Source,
                Direction = operation.Direction,
                AccountingType = operation.AccountingType,
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
                CreatedAt = operation.CreatedAt,
                UpdatedAt = operation.UpdatedAt,
                IsReadonly = false,
                RelatedPaymentId = null,
            });

        var paymentOperations = ApplyPaymentFilters(
                _db.Payments.AsNoTracking(),
                from,
                to,
                direction,
                accountingType,
                search)
            .Select(payment => new MoneyOperationListItemDto
            {
                Id = -payment.Id,
                Source =
                    payment.PaymentSource != null &&
                    payment.PaymentSource.Name != null &&
                    payment.PaymentSource.Name.ToLower().Contains(CashSourceToken)
                        ? MoneyOperationSource.Cash
                        : payment.PaymentSource != null &&
                          payment.PaymentSource.Name != null &&
                          payment.PaymentSource.Name.ToLower().Contains(CardSourceToken)
                            ? MoneyOperationSource.Card
                            : MoneyOperationSource.Bank,
                Direction = payment.Type == PaymentType.Income
                    ? MoneyOperationDirection.Income
                    : MoneyOperationDirection.Expense,
                AccountingType = payment.Status == PaymentStatus.Completed
                    ? MoneyOperationAccountingType.Fact
                    : MoneyOperationAccountingType.Plan,
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
                CreatedAt = payment.CreatedAt,
                UpdatedAt = null,
                IsReadonly = true,
                RelatedPaymentId = payment.Id,
            });

        var combinedQuery = customOperations.Concat(paymentOperations);

        if (source.HasValue)
        {
            combinedQuery = combinedQuery.Where(item => item.Source == source.Value);
        }

        return combinedQuery;
    }

    private static IQueryable<MoneyOperation> ApplyMoneyOperationFilters(
        IQueryable<MoneyOperation> query,
        DateTime? from,
        DateTime? to,
        MoneyOperationSource? source,
        MoneyOperationDirection? direction,
        MoneyOperationAccountingType? accountingType,
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

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(operation =>
                (operation.CounterpartyName ?? string.Empty).ToLower().Contains(term) ||
                (operation.Purpose ?? string.Empty).ToLower().Contains(term) ||
                (operation.CategoryName ?? string.Empty).ToLower().Contains(term));
        }

        return query;
    }

    private static IQueryable<Payment> ApplyPaymentFilters(
        IQueryable<Payment> query,
        DateTime? from,
        DateTime? to,
        MoneyOperationDirection? direction,
        MoneyOperationAccountingType? accountingType,
        string? search)
    {
        query = query.Where(payment => payment.Status == PaymentStatus.Completed);

        if (from.HasValue)
        {
            var fromDate = from.Value.Date;
            query = query.Where(payment => payment.Date >= fromDate);
        }

        if (to.HasValue)
        {
            var toDate = to.Value.Date;
            query = query.Where(payment => payment.Date <= toDate);
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

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(payment =>
                (payment.CounterpartyName ?? string.Empty).ToLower().Contains(term) ||
                (payment.Description ?? string.Empty).ToLower().Contains(term) ||
                (payment.Account ?? string.Empty).ToLower().Contains(term));
        }

        return query;
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id, CancellationToken ct)
    {
        var item = await _db.MoneyOperations.AsNoTracking().FirstOrDefaultAsync(operation => operation.Id == id, ct);
        return item is null ? NotFound() : Ok(item);
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
        model.CreatedAt = DateTime.UtcNow;

        _db.MoneyOperations.Add(model);
        await _db.SaveChangesAsync();
        return Created($"/api/money-operations/{model.Id}", model);
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
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
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

        _db.MoneyOperations.Remove(entity);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
