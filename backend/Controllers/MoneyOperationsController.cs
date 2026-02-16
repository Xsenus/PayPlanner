using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Helpers;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/money-operations")]
[Authorize]
public class MoneyOperationsController : ControllerBase
{
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
        var list = await SqliteRetryHelper.ExecuteAsync(async cancellationToken =>
        {
            var customQuery = ApplyMoneyOperationFilters(
                _db.MoneyOperations.AsNoTracking().AsQueryable(),
                from,
                to,
                source,
                direction,
                accountingType,
                search);

            var customOperations = (await customQuery
                .OrderByDescending(o => o.Date)
                .ThenByDescending(o => o.Id)
                .ToListAsync(cancellationToken))
                .Select(MapManualOperationToListItem)
                .ToList();

            var paymentsQuery = ApplyPaymentFilters(
                _db.Payments
                    .AsNoTracking()
                    .Include(p => p.PaymentSource)
                    .Include(p => p.ClientCase)
                    .AsQueryable(),
                from,
                to,
                direction,
                accountingType,
                search);

            var paymentOperations = (await paymentsQuery.ToListAsync(cancellationToken))
                .Select(MapPaymentToListItem)
                .Where(o => !source.HasValue || o.Source == source.Value)
                .ToList();

            return customOperations
                .Concat(paymentOperations)
                .OrderByDescending(o => o.Date)
                .ThenByDescending(o => o.Id)
                .ToList();
        }, _logger, ct);

        return Ok(list);
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
            query = query.Where(o => o.Date >= fromDate);
        }

        if (to.HasValue)
        {
            var toDate = to.Value.Date;
            query = query.Where(o => o.Date <= toDate);
        }

        if (source.HasValue)
        {
            query = query.Where(o => o.Source == source.Value);
        }

        if (direction.HasValue)
        {
            query = query.Where(o => o.Direction == direction.Value);
        }

        if (accountingType.HasValue)
        {
            query = query.Where(o => o.AccountingType == accountingType.Value);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(o =>
                (o.CounterpartyName ?? string.Empty).ToLower().Contains(term) ||
                (o.Purpose ?? string.Empty).ToLower().Contains(term) ||
                (o.CategoryName ?? string.Empty).ToLower().Contains(term));
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
        if (from.HasValue)
        {
            var fromDate = from.Value.Date;
            query = query.Where(p => p.Date >= fromDate);
        }

        if (to.HasValue)
        {
            var toDate = to.Value.Date;
            query = query.Where(p => p.Date <= toDate);
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
                query = query.Where(p => p.Type == mappedType.Value);
            }
            else
            {
                query = query.Where(_ => false);
            }
        }

        if (accountingType.HasValue)
        {
            if (accountingType.Value == MoneyOperationAccountingType.Fact)
            {
                query = query.Where(p => p.Status == PaymentStatus.Completed);
            }
            else
            {
                query = query.Where(p => p.Status != PaymentStatus.Completed);
            }
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(p =>
                (p.CounterpartyName ?? string.Empty).ToLower().Contains(term) ||
                (p.Description ?? string.Empty).ToLower().Contains(term) ||
                (p.Account ?? string.Empty).ToLower().Contains(term));
        }

        return query;
    }

    private static MoneyOperationListItem MapManualOperationToListItem(MoneyOperation operation) => new()
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
    };

    private static MoneyOperationListItem MapPaymentToListItem(Payment payment)
    {
        var paymentSource = (payment.PaymentSource?.Name ?? string.Empty).Trim().ToLowerInvariant();
        var source = paymentSource.Contains("кас")
            ? MoneyOperationSource.Cash
            : paymentSource.Contains("карт")
                ? MoneyOperationSource.Card
                : MoneyOperationSource.Bank;

        var accountingType = payment.Status == PaymentStatus.Completed
            ? MoneyOperationAccountingType.Fact
            : MoneyOperationAccountingType.Plan;

        var operationDate = payment.Status == PaymentStatus.Completed
            ? (payment.LastPaymentDate ?? payment.PaidDate ?? payment.Date)
            : payment.Date;

        var amount = accountingType == MoneyOperationAccountingType.Fact && payment.PaidAmount > 0m
            ? payment.PaidAmount
            : payment.Amount;

        return new MoneyOperationListItem
        {
            Id = -payment.Id,
            Source = source,
            Direction = payment.Type == PaymentType.Income ? MoneyOperationDirection.Income : MoneyOperationDirection.Expense,
            AccountingType = accountingType,
            AccountName = payment.Account,
            CounterpartyName = payment.CounterpartyName,
            CategoryName = payment.ClientCase?.Title,
            Amount = amount,
            Currency = "RUB",
            Date = operationDate,
            RecognitionDate = payment.Date,
            IsAdvancePayment = false,
            Purpose = payment.Description,
            PaidInvoiceNumber = payment.Account,
            PaidAmount = payment.PaidAmount,
            ProjectName = payment.ClientCase?.Title,
            DirectionName = payment.PaymentSource?.Name,
            SalesPointName = null,
            PaymentPriority = null,
            PaidAccountName = payment.Account,
            CreatedAt = payment.CreatedAt,
            UpdatedAt = null,
            IsReadonly = true,
            RelatedPaymentId = payment.Id,
        };
    }

    private sealed class MoneyOperationListItem : MoneyOperation
    {
        public bool IsReadonly { get; init; }
        public int? RelatedPaymentId { get; init; }
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id, CancellationToken ct)
    {
        var item = await _db.MoneyOperations.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, ct);
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
        if (entity is null) return NotFound();

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
        if (entity is null) return NotFound();
        _db.MoneyOperations.Remove(entity);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
