using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/cash-orders")]
[Authorize]
public class CashOrdersController : ControllerBase
{
    private const string CashSourceToken = "налич";
    private const string CashRegisterSourceToken = "кас";
    private readonly PaymentContext _db;

    public CashOrdersController(PaymentContext db)
    {
        _db = db;
    }

    [HttpGet("paged")]
    public async Task<ActionResult<PagedResponse<CashOrderDto>>> GetPaged(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] CashOrderType? type,
        [FromQuery] CashOrderStatus? status,
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 5, 200);

        var query = _db.CashOrders
            .Include(order => order.Client)
            .Include(order => order.Payment)
            .AsNoTracking()
            .AsQueryable();

        if (from.HasValue)
        {
            query = query.Where(order => order.Date >= from.Value.Date);
        }

        if (to.HasValue)
        {
            query = query.Where(order => order.Date <= to.Value.Date);
        }

        if (type.HasValue)
        {
            query = query.Where(order => order.Type == type.Value);
        }

        if (status.HasValue)
        {
            query = query.Where(order => order.Status == status.Value);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var titleTerm = ToTitleSearchTerm(term);
            query = query.Where(order =>
                order.Number.Contains(term) ||
                (order.PayerName ?? string.Empty).Contains(term) ||
                (order.Basis ?? string.Empty).Contains(term) ||
                (order.Account ?? string.Empty).Contains(term) ||
                (titleTerm != term &&
                    (order.Number.Contains(titleTerm) ||
                     (order.PayerName ?? string.Empty).Contains(titleTerm) ||
                     (order.Basis ?? string.Empty).Contains(titleTerm) ||
                     (order.Account ?? string.Empty).Contains(titleTerm))));
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(order => order.Date)
            .ThenByDescending(order => order.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(order => Map(order))
            .ToListAsync(ct);

        return Ok(new PagedResponse<CashOrderDto>
        {
            Items = items,
            Total = total,
            Page = page,
            PageSize = pageSize,
        });
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id, CancellationToken ct)
    {
        var order = await _db.CashOrders
            .Include(item => item.Client)
            .Include(item => item.Payment)
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == id, ct);

        return order is null ? NotFound() : Ok(Map(order));
    }

    [HttpPost("from-payment/{paymentId:int}")]
    public async Task<IActionResult> CreateFromPayment(int paymentId, CancellationToken ct)
    {
        var payment = await _db.Payments
            .Include(item => item.Client)
            .Include(item => item.PaymentSource)
            .FirstOrDefaultAsync(item => item.Id == paymentId, ct);

        if (payment is null)
        {
            return NotFound();
        }

        if (payment.Status != PaymentStatus.Completed || payment.PaidAmount <= 0m)
        {
            return BadRequest("ПКО можно создать только по выполненному наличному платежу с фактической оплатой.");
        }

        if (!IsCashPayment(payment))
        {
            return BadRequest("ПКО создается только для платежей с источником оплаты наличными.");
        }

        var existing = await _db.CashOrders
            .Where(order => order.PaymentId == payment.Id && order.Status != CashOrderStatus.Cancelled)
            .OrderByDescending(order => order.Id)
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            return Ok(Map(existing));
        }

        var orderType = payment.Type == PaymentType.Expense ? CashOrderType.Outgoing : CashOrderType.Incoming;
        var orderDate = (payment.LastPaymentDate ?? payment.PaidDate ?? payment.Date).Date;
        var order = new CashOrder
        {
            Number = await GenerateNumberAsync(orderType, orderDate, ct),
            Type = orderType,
            Status = CashOrderStatus.Posted,
            Date = orderDate,
            Amount = payment.PaidAmount,
            Currency = "RUB",
            ClientId = payment.ClientId ?? payment.ExpenseClientId,
            PaymentId = payment.Id,
            InvoiceId = payment.InvoiceId,
            Account = payment.Account,
            PayerName = payment.CounterpartyName ?? payment.Client?.Name,
            Basis = BuildBasis(payment),
            CreatedAt = DateTime.UtcNow,
        };

        _db.CashOrders.Add(order);
        await _db.SaveChangesAsync(ct);

        return Created($"/api/cash-orders/{order.Id}", Map(order));
    }

    [HttpPost("from-money-operation/{operationId:int}")]
    public async Task<IActionResult> CreateFromMoneyOperation(int operationId, CancellationToken ct)
    {
        var operation = await _db.MoneyOperations.FirstOrDefaultAsync(item => item.Id == operationId, ct);
        if (operation is null)
        {
            return NotFound();
        }

        if (!ShouldCreateCashOrder(operation))
        {
            return BadRequest("Кассовый ордер можно создать только по фактической операции кассы с суммой больше нуля.");
        }

        var existing = await _db.CashOrders
            .Where(order => order.MoneyOperationId == operation.Id && order.Status != CashOrderStatus.Cancelled)
            .OrderByDescending(order => order.Id)
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            return Ok(Map(existing));
        }

        var orderType = operation.Direction == MoneyOperationDirection.Expense ? CashOrderType.Outgoing : CashOrderType.Incoming;
        var order = new CashOrder
        {
            Number = await GenerateNumberAsync(orderType, operation.Date, ct),
            Type = orderType,
            Status = CashOrderStatus.Posted,
            Date = operation.Date.Date,
            Amount = operation.Amount,
            Currency = string.IsNullOrWhiteSpace(operation.Currency) ? "RUB" : operation.Currency,
            MoneyOperationId = operation.Id,
            Account = operation.AccountName,
            PayerName = operation.CounterpartyName,
            Basis = BuildBasis(operation),
            Comment = operation.CategoryName,
            CreatedAt = DateTime.UtcNow,
        };

        _db.CashOrders.Add(order);
        await _db.SaveChangesAsync(ct);

        return Created($"/api/cash-orders/{order.Id}", Map(order));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CashOrder model, CancellationToken ct)
    {
        if (model.Amount <= 0m)
        {
            return BadRequest("Сумма кассового ордера должна быть больше нуля.");
        }

        model.Number = string.IsNullOrWhiteSpace(model.Number)
            ? await GenerateNumberAsync(model.Type, model.Date == default ? DateTime.Today : model.Date, ct)
            : model.Number.Trim();
        model.Currency = string.IsNullOrWhiteSpace(model.Currency) ? "RUB" : model.Currency.Trim();
        model.Account = Normalize(model.Account);
        model.PayerName = Normalize(model.PayerName);
        model.Basis = Normalize(model.Basis);
        model.Comment = Normalize(model.Comment);
        model.CreatedAt = DateTime.UtcNow;

        _db.CashOrders.Add(model);
        await _db.SaveChangesAsync(ct);

        return Created($"/api/cash-orders/{model.Id}", Map(model));
    }

    [HttpGet("{id:int}/print")]
    public async Task<IActionResult> Print(int id, CancellationToken ct)
    {
        var order = await _db.CashOrders
            .Include(item => item.Client)
            .Include(item => item.Payment)
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (order is null)
        {
            return NotFound();
        }

        return Content(BuildPrintHtml(order), "text/html; charset=utf-8", Encoding.UTF8);
    }

    private static bool IsCashPayment(Payment payment)
        => payment.PaymentSource?.Name?.Contains(CashSourceToken, StringComparison.OrdinalIgnoreCase) == true ||
           payment.PaymentSource?.Name?.Contains(CashRegisterSourceToken, StringComparison.OrdinalIgnoreCase) == true;

    private static bool ShouldCreateCashOrder(MoneyOperation operation)
        => operation.Source == MoneyOperationSource.Cash &&
           operation.AccountingType == MoneyOperationAccountingType.Fact &&
           operation.Amount > 0m &&
           (operation.Direction == MoneyOperationDirection.Income ||
            operation.Direction == MoneyOperationDirection.Expense);

    private async Task<string> GenerateNumberAsync(CashOrderType type, DateTime date, CancellationToken ct)
    {
        var prefix = type == CashOrderType.Incoming ? "ПКО" : "РКО";
        var yearPrefix = $"{prefix}-{date:yyyy}-";
        var count = await _db.CashOrders.CountAsync(order => order.Number.StartsWith(yearPrefix), ct);
        return $"{yearPrefix}{count + 1:0000}";
    }

    private static string BuildBasis(Payment payment)
    {
        var account = string.IsNullOrWhiteSpace(payment.Account) ? null : $"счет №{payment.Account}";
        var description = string.IsNullOrWhiteSpace(payment.Description) ? null : payment.Description.Trim();
        return string.Join(". ", new[] { "Оплата от клиента", account, description }.Where(item => !string.IsNullOrWhiteSpace(item)));
    }

    private static string BuildBasis(MoneyOperation operation)
    {
        var source = operation.Direction == MoneyOperationDirection.Expense
            ? "Выдача из кассы"
            : "Прием наличных в кассу";
        return string.Join(". ", new[] { source, operation.Purpose, operation.PaidInvoiceNumber }.Where(item => !string.IsNullOrWhiteSpace(item)));
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string ToTitleSearchTerm(string value)
        => string.IsNullOrWhiteSpace(value)
            ? value
            : string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Length == 1
                    ? part.ToUpperInvariant()
                    : char.ToUpperInvariant(part[0]) + part[1..]));

    private static CashOrderDto Map(CashOrder order)
        => new()
        {
            Id = order.Id,
            Number = order.Number,
            Type = order.Type,
            Status = order.Status,
            Date = order.Date,
            Amount = order.Amount,
            Currency = order.Currency,
            ClientId = order.ClientId,
            ClientName = order.Client?.Name,
            PaymentId = order.PaymentId,
            MoneyOperationId = order.MoneyOperationId,
            InvoiceId = order.InvoiceId,
            Account = order.Account,
            PayerName = order.PayerName,
            Basis = order.Basis,
            Comment = order.Comment,
            CreatedAt = order.CreatedAt,
            UpdatedAt = order.UpdatedAt,
        };

    private static string BuildPrintHtml(CashOrder order)
    {
        var title = order.Type == CashOrderType.Incoming ? "Приходный кассовый ордер" : "Расходный кассовый ордер";
        var amount = order.Amount.ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
        var payer = System.Net.WebUtility.HtmlEncode(order.PayerName ?? order.Client?.Name ?? "-");
        var basis = System.Net.WebUtility.HtmlEncode(order.Basis ?? "-");
        var account = System.Net.WebUtility.HtmlEncode(order.Account ?? "-");
        var comment = System.Net.WebUtility.HtmlEncode(order.Comment ?? "-");
        return $$"""
<!doctype html>
<html lang="ru">
<head>
  <meta charset="utf-8" />
  <title>{{title}} {{System.Net.WebUtility.HtmlEncode(order.Number)}}</title>
  <style>
    body { font-family: Arial, sans-serif; color: #111827; margin: 32px; }
    .doc { max-width: 860px; margin: 0 auto; border: 1px solid #cbd5e1; padding: 28px; }
    h1 { margin: 0 0 16px; font-size: 22px; }
    table { width: 100%; border-collapse: collapse; margin-top: 18px; }
    td { border: 1px solid #cbd5e1; padding: 10px; vertical-align: top; }
    .label { width: 220px; color: #64748b; }
    .signatures { display: grid; grid-template-columns: 1fr 1fr; gap: 36px; margin-top: 44px; }
    .line { border-top: 1px solid #111827; padding-top: 8px; }
  </style>
</head>
<body>
  <div class="doc">
    <h1>{{title}} № {{System.Net.WebUtility.HtmlEncode(order.Number)}}</h1>
    <div>Дата: {{order.Date:dd.MM.yyyy}}</div>
    <table>
      <tr><td class="label">Сумма</td><td>{{amount}} руб.</td></tr>
      <tr><td class="label">Принято от</td><td>{{payer}}</td></tr>
      <tr><td class="label">Основание</td><td>{{basis}}</td></tr>
      <tr><td class="label">Счет</td><td>{{account}}</td></tr>
      <tr><td class="label">Комментарий</td><td>{{comment}}</td></tr>
    </table>
    <div class="signatures">
      <div class="line">Главный бухгалтер</div>
      <div class="line">Кассир</div>
    </div>
  </div>
</body>
</html>
""";
    }
}
