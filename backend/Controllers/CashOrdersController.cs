using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.Invoices;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/cash-orders")]
[Authorize]
public class CashOrdersController : ControllerBase
{
    private const string CashSourceToken = "налич";
    private const string CashRegisterSourceToken = "кас";
    private readonly PaymentContext _db;
    private readonly IInvoiceDocumentPdfService? _pdfService;

    public CashOrdersController(PaymentContext db, IInvoiceDocumentPdfService? pdfService = null)
    {
        _db = db;
        _pdfService = pdfService;
    }

    [HttpGet("cashbook")]
    public async Task<ActionResult<CashBookResponseDto>> GetCashBook(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        CancellationToken ct = default)
    {
        return Ok(await BuildCashBookAsync(from, to, ct));
    }

    [HttpGet("cashbook/print")]
    public async Task<IActionResult> PrintCashBook(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        CancellationToken ct = default)
    {
        var cashBook = await BuildCashBookAsync(from, to, ct);
        return Content(BuildCashBookPrintHtml(cashBook), "text/html; charset=utf-8", Encoding.UTF8);
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

        var query = BuildFilteredQuery(from, to, type, status, search);

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

    [HttpGet("register")]
    public async Task<ActionResult<CashOrderRegisterResponseDto>> GetRegister(
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

        var query = BuildFilteredQuery(from, to, type, status, search);
        var total = await query.CountAsync(ct);
        var summary = await query
            .GroupBy(_ => 1)
            .Select(group => new
            {
                IncomingCount = group.Count(order => order.Type == CashOrderType.Incoming),
                OutgoingCount = group.Count(order => order.Type == CashOrderType.Outgoing),
                PostedCount = group.Count(order => order.Status == CashOrderStatus.Posted),
                CancelledCount = group.Count(order => order.Status == CashOrderStatus.Cancelled),
                IncomingAmount = group
                    .Where(order => order.Type == CashOrderType.Incoming && order.Status == CashOrderStatus.Posted)
                    .Sum(order => order.Amount),
                OutgoingAmount = group
                    .Where(order => order.Type == CashOrderType.Outgoing && order.Status == CashOrderStatus.Posted)
                    .Sum(order => order.Amount),
            })
            .FirstOrDefaultAsync(ct);

        var items = await query
            .OrderByDescending(order => order.Date)
            .ThenByDescending(order => order.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(order => Map(order))
            .ToListAsync(ct);

        return Ok(new CashOrderRegisterResponseDto
        {
            Items = items,
            Total = total,
            Page = page,
            PageSize = pageSize,
            IncomingCount = summary?.IncomingCount ?? 0,
            OutgoingCount = summary?.OutgoingCount ?? 0,
            PostedCount = summary?.PostedCount ?? 0,
            CancelledCount = summary?.CancelledCount ?? 0,
            IncomingAmount = summary?.IncomingAmount ?? 0m,
            OutgoingAmount = summary?.OutgoingAmount ?? 0m,
        });
    }

    [HttpGet("register/print")]
    public async Task<IActionResult> PrintRegister(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] CashOrderType? type,
        [FromQuery] CashOrderStatus? status,
        [FromQuery] string? search,
        CancellationToken ct = default)
    {
        var query = BuildFilteredQuery(from, to, type, status, search);
        var total = await query.CountAsync(ct);
        var summary = await query
            .GroupBy(_ => 1)
            .Select(group => new
            {
                IncomingCount = group.Count(order => order.Type == CashOrderType.Incoming),
                OutgoingCount = group.Count(order => order.Type == CashOrderType.Outgoing),
                PostedCount = group.Count(order => order.Status == CashOrderStatus.Posted),
                CancelledCount = group.Count(order => order.Status == CashOrderStatus.Cancelled),
                IncomingAmount = group
                    .Where(order => order.Type == CashOrderType.Incoming && order.Status == CashOrderStatus.Posted)
                    .Sum(order => order.Amount),
                OutgoingAmount = group
                    .Where(order => order.Type == CashOrderType.Outgoing && order.Status == CashOrderStatus.Posted)
                    .Sum(order => order.Amount),
            })
            .FirstOrDefaultAsync(ct);
        var items = await query
            .OrderBy(order => order.Date)
            .ThenBy(order => order.Id)
            .Take(2_000)
            .Select(order => Map(order))
            .ToListAsync(ct);

        var register = new CashOrderRegisterResponseDto
        {
            Items = items,
            Total = total,
            Page = 1,
            PageSize = items.Count,
            IncomingCount = summary?.IncomingCount ?? 0,
            OutgoingCount = summary?.OutgoingCount ?? 0,
            PostedCount = summary?.PostedCount ?? 0,
            CancelledCount = summary?.CancelledCount ?? 0,
            IncomingAmount = summary?.IncomingAmount ?? 0m,
            OutgoingAmount = summary?.OutgoingAmount ?? 0m,
        };

        return Content(BuildRegisterPrintHtml(register, from, to), "text/html; charset=utf-8", Encoding.UTF8);
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
            ReceiverName = payment.Type == PaymentType.Expense ? payment.CounterpartyName ?? payment.Client?.Name : null,
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
            ReceiverName = operation.Direction == MoneyOperationDirection.Expense ? operation.CounterpartyName : null,
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
        model.ReceiverName = Normalize(model.ReceiverName);
        model.Basis = Normalize(model.Basis);
        model.Appendix = Normalize(model.Appendix);
        model.Comment = Normalize(model.Comment);
        model.CancelledAt = model.Status == CashOrderStatus.Cancelled ? model.CancelledAt ?? DateTime.UtcNow : null;
        model.CancelledReason = model.Status == CashOrderStatus.Cancelled ? Normalize(model.CancelledReason) : null;
        model.CreatedAt = DateTime.UtcNow;

        _db.CashOrders.Add(model);
        await _db.SaveChangesAsync(ct);

        return Created($"/api/cash-orders/{model.Id}", Map(model));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] CashOrder model, CancellationToken ct)
    {
        var order = await _db.CashOrders.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (order is null)
        {
            return NotFound();
        }

        if (order.Status == CashOrderStatus.Cancelled)
        {
            return BadRequest("Отменённый кассовый ордер нельзя редактировать.");
        }

        if (model.Amount <= 0m)
        {
            return BadRequest("Сумма кассового ордера должна быть больше нуля.");
        }

        order.Type = model.Type;
        order.Date = model.Date == default ? order.Date : model.Date.Date;
        order.Amount = model.Amount;
        order.Currency = string.IsNullOrWhiteSpace(model.Currency) ? "RUB" : model.Currency.Trim();
        order.ClientId = model.ClientId;
        order.PaymentId = model.PaymentId;
        order.MoneyOperationId = model.MoneyOperationId;
        order.InvoiceId = model.InvoiceId;
        order.Account = Normalize(model.Account);
        order.PayerName = Normalize(model.PayerName);
        order.ReceiverName = Normalize(model.ReceiverName);
        order.Basis = Normalize(model.Basis);
        order.Appendix = Normalize(model.Appendix);
        order.Comment = Normalize(model.Comment);
        order.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        return Ok(Map(order));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var order = await _db.CashOrders.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (order is null)
        {
            return NotFound();
        }

        if (order.Status != CashOrderStatus.Draft)
        {
            return BadRequest("Удалить можно только черновик кассового ордера. Проведённый документ нужно отменить.");
        }

        _db.CashOrders.Remove(order);
        await _db.SaveChangesAsync(ct);

        return NoContent();
    }

    [HttpPost("{id:int}/cancel")]
    public async Task<IActionResult> Cancel(int id, [FromBody] CashOrderCancelRequest? request, CancellationToken ct)
    {
        var order = await _db.CashOrders.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (order is null)
        {
            return NotFound();
        }

        if (order.Status != CashOrderStatus.Cancelled)
        {
            order.Status = CashOrderStatus.Cancelled;
            order.CancelledAt = DateTime.UtcNow;
            order.CancelledReason = Normalize(request?.Reason);
            order.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        return Ok(Map(order));
    }

    [HttpPost("{id:int}/copy")]
    public async Task<IActionResult> Copy(int id, CancellationToken ct)
    {
        var source = await _db.CashOrders
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (source is null)
        {
            return NotFound();
        }

        var order = new CashOrder
        {
            Number = await GenerateNumberAsync(source.Type, DateTime.Today, ct),
            Type = source.Type,
            Status = CashOrderStatus.Posted,
            Date = DateTime.Today,
            Amount = source.Amount,
            Currency = string.IsNullOrWhiteSpace(source.Currency) ? "RUB" : source.Currency,
            ClientId = source.ClientId,
            InvoiceId = source.InvoiceId,
            Account = source.Account,
            PayerName = source.PayerName,
            ReceiverName = source.ReceiverName,
            Basis = source.Basis,
            Appendix = source.Appendix,
            Comment = source.Comment,
            CreatedAt = DateTime.UtcNow,
        };

        _db.CashOrders.Add(order);
        await _db.SaveChangesAsync(ct);

        return Created($"/api/cash-orders/{order.Id}", Map(order));
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

    [HttpGet("{id:int}/pdf")]
    public async Task<IActionResult> DownloadPdf(int id, CancellationToken ct)
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

        try
        {
            var pdfBytes = await RenderPdfAsync(BuildPrintHtml(order), ct);
            return File(pdfBytes, "application/pdf", $"cash-order-{SanitizeFilePart(order.Number)}.pdf");
        }
        catch (InvalidOperationException ex)
        {
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, detail: ex.Message);
        }
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

    private async Task<CashBookResponseDto> BuildCashBookAsync(DateTime? from, DateTime? to, CancellationToken ct)
    {
        var fromDate = (from ?? DateTime.Today).Date;
        var toDate = (to ?? fromDate).Date;
        if (toDate < fromDate)
        {
            (fromDate, toDate) = (toDate, fromDate);
        }

        var postedOrders = _db.CashOrders
            .AsNoTracking()
            .Where(order => order.Status == CashOrderStatus.Posted);

        var openingBalance = await postedOrders
            .Where(order => order.Date < fromDate)
            .SumAsync(order => order.Type == CashOrderType.Incoming ? order.Amount : -order.Amount, ct);

        var periodGroups = await postedOrders
            .Where(order => order.Date >= fromDate && order.Date <= toDate)
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

        var runningBalance = openingBalance;
        var days = new List<CashBookDayDto>(periodGroups.Count);
        foreach (var group in periodGroups)
        {
            var dayOpening = runningBalance;
            runningBalance += group.Income - group.Expense;
            days.Add(new CashBookDayDto
            {
                Date = group.Date,
                OpeningBalance = dayOpening,
                Income = group.Income,
                Expense = group.Expense,
                ClosingBalance = runningBalance,
                IncomingCount = group.IncomingCount,
                OutgoingCount = group.OutgoingCount,
            });
        }

        var income = periodGroups.Sum(group => group.Income);
        var expense = periodGroups.Sum(group => group.Expense);

        return new CashBookResponseDto
        {
            From = fromDate,
            To = toDate,
            OpeningBalance = openingBalance,
            Income = income,
            Expense = expense,
            ClosingBalance = openingBalance + income - expense,
            IncomingCount = periodGroups.Sum(group => group.IncomingCount),
            OutgoingCount = periodGroups.Sum(group => group.OutgoingCount),
            Days = days,
        };
    }

    private IQueryable<CashOrder> BuildFilteredQuery(
        DateTime? from,
        DateTime? to,
        CashOrderType? type,
        CashOrderStatus? status,
        string? search)
    {
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
                (order.ReceiverName ?? string.Empty).Contains(term) ||
                (order.Basis ?? string.Empty).Contains(term) ||
                (order.Appendix ?? string.Empty).Contains(term) ||
                (order.Account ?? string.Empty).Contains(term) ||
                (titleTerm != term &&
                    (order.Number.Contains(titleTerm) ||
                     (order.PayerName ?? string.Empty).Contains(titleTerm) ||
                     (order.ReceiverName ?? string.Empty).Contains(titleTerm) ||
                     (order.Basis ?? string.Empty).Contains(titleTerm) ||
                     (order.Appendix ?? string.Empty).Contains(titleTerm) ||
                     (order.Account ?? string.Empty).Contains(titleTerm))));
        }

        return query;
    }

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

    private Task<byte[]> RenderPdfAsync(string documentHtml, CancellationToken ct)
    {
        if (_pdfService is null)
        {
            throw new InvalidOperationException(
                "PDF-генератор не зарегистрирован на сервере. Проверьте настройки приложения.");
        }

        return _pdfService.RenderAsync(documentHtml, ct);
    }

    private static string SanitizeFilePart(string? value)
    {
        var raw = string.IsNullOrWhiteSpace(value) ? "cash-order" : value.Trim();
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(raw.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "cash-order" : sanitized;
    }

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
            ReceiverName = order.ReceiverName,
            Basis = order.Basis,
            Appendix = order.Appendix,
            Comment = order.Comment,
            CancelledAt = order.CancelledAt,
            CancelledReason = order.CancelledReason,
            CreatedAt = order.CreatedAt,
            UpdatedAt = order.UpdatedAt,
        };

    private static string BuildPrintHtml(CashOrder order)
    {
        var isIncoming = order.Type == CashOrderType.Incoming;
        var title = isIncoming ? "Приходный кассовый ордер" : "Расходный кассовый ордер";
        var formName = isIncoming ? "Унифицированная форма № КО-1" : "Унифицированная форма № КО-2";
        var okud = isIncoming ? "0310001" : "0310002";
        var personLabel = isIncoming ? "Принято от" : "Выдать";
        var signatureLabel = isIncoming ? "Принял" : "Получил";
        var amount = order.Amount.ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
        var amountWords = Html(AmountToWordsRu(order.Amount));
        var payer = System.Net.WebUtility.HtmlEncode(order.PayerName ?? order.Client?.Name ?? "-");
        var receiver = System.Net.WebUtility.HtmlEncode(order.ReceiverName ?? "-");
        var basis = System.Net.WebUtility.HtmlEncode(order.Basis ?? "-");
        var appendix = System.Net.WebUtility.HtmlEncode(order.Appendix ?? "-");
        var account = System.Net.WebUtility.HtmlEncode(order.Account ?? "-");
        var comment = System.Net.WebUtility.HtmlEncode(order.Comment ?? "-");
        var cancelNotice = order.Status == CashOrderStatus.Cancelled
            ? $"""<div class="cancelled">Документ отменён{(order.CancelledAt.HasValue ? $" {order.CancelledAt:dd.MM.yyyy HH:mm}" : string.Empty)}{(string.IsNullOrWhiteSpace(order.CancelledReason) ? string.Empty : $": {System.Net.WebUtility.HtmlEncode(order.CancelledReason)}")}</div>"""
            : string.Empty;
        var receiptHtml = isIncoming
            ? $$"""
    <section class="receipt">
      <h2>Квитанция к приходному кассовому ордеру № {{System.Net.WebUtility.HtmlEncode(order.Number)}}</h2>
      <div class="field"><div class="label">Принято от</div><div class="line">{{payer}}</div></div>
      <div class="field"><div class="label">Получатель</div><div class="line">{{receiver}}</div></div>
      <div class="field"><div class="label">Основание</div><div class="line">{{basis}}</div></div>
      <div class="amount-box">
        <div class="label">Сумма</div>
        <div class="line">{{amountWords}}</div>
        <div class="amount">{{amount}} руб.</div>
      </div>
      <div class="signatures">
        <div class="signature">Бухгалтер</div>
        <div class="signature">Кассир</div>
        <div class="signature">М.П.</div>
      </div>
    </section>
"""
            : string.Empty;
        return $$"""
<!doctype html>
<html lang="ru">
<head>
  <meta charset="utf-8" />
  <title>{{title}} {{System.Net.WebUtility.HtmlEncode(order.Number)}}</title>
  <style>
    @page { size: A4 portrait; margin: 14mm; }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      color: #111827;
      font-family: Arial, "DejaVu Sans", sans-serif;
      font-size: 12px;
      line-height: 1.25;
      background: #fff;
      -webkit-print-color-adjust: exact !important;
      print-color-adjust: exact !important;
    }
    .doc { max-width: 820px; margin: 0 auto; }
    .top { display: grid; grid-template-columns: 1fr 190px; gap: 20px; align-items: start; }
    .meta { border: 1px solid #111827; border-collapse: collapse; width: 100%; font-size: 12px; }
    .meta td { border: 1px solid #111827; padding: 6px 8px; }
    .meta .value { font-size: 18px; font-weight: 700; text-align: center; }
    .muted { color: #64748b; font-size: 12px; }
    h1 { margin: 22px 0 18px; text-align: center; font-size: 24px; line-height: 1.2; }
    h2 { margin: 0 0 12px; text-align: center; font-size: 18px; }
    .number { white-space: nowrap; }
    .field { display: grid; grid-template-columns: 170px 1fr; gap: 12px; margin-top: 12px; font-size: 15px; }
    .label { color: #475569; }
    .line { min-height: 22px; border-bottom: 1px solid #111827; padding-bottom: 3px; }
    .amount-box { display: grid; grid-template-columns: 170px 1fr 180px; gap: 12px; margin-top: 18px; align-items: end; }
    .amount { border: 1px solid #111827; padding: 8px 10px; text-align: right; font-size: 18px; font-weight: 700; }
    .notice { margin-top: 18px; border: 1px solid #cbd5e1; padding: 10px 12px; color: #334155; font-size: 13px; }
    .cancelled { margin-top: 18px; border: 1px solid #fecdd3; background: #fff1f2; color: #be123c; padding: 10px 12px; font-weight: 700; }
    .receipt { margin-top: 28px; border-top: 2px dashed #cbd5e1; padding-top: 20px; }
    .signatures { display: grid; grid-template-columns: repeat(4, 1fr); gap: 22px; margin-top: 42px; }
    .receipt .signatures { grid-template-columns: repeat(3, 1fr); }
    .signature { border-top: 1px solid #111827; padding-top: 7px; color: #334155; font-size: 13px; text-align: center; }
    .signature small { display: block; margin-top: 5px; color: #64748b; font-size: 11px; }
  </style>
</head>
<body>
  <div class="doc">
    <div class="top">
      <div class="muted">{{formName}}</div>
      <table class="meta">
        <tr><td>Форма</td><td>{{formName}}</td></tr>
        <tr><td>ОКУД</td><td>{{okud}}</td></tr>
        <tr><td>Номер</td><td class="value">{{System.Net.WebUtility.HtmlEncode(order.Number)}}</td></tr>
        <tr><td>Дата</td><td>{{order.Date:dd.MM.yyyy}}</td></tr>
      </table>
    </div>

    <h1>{{title}} <span class="number">№ {{System.Net.WebUtility.HtmlEncode(order.Number)}}</span></h1>

    {{cancelNotice}}
    <div class="field"><div class="label">{{personLabel}}</div><div class="line">{{payer}}</div></div>
    <div class="field"><div class="label">Получатель</div><div class="line">{{receiver}}</div></div>
    <div class="field"><div class="label">Основание</div><div class="line">{{basis}}</div></div>
    <div class="field"><div class="label">Приложение</div><div class="line">{{appendix}}</div></div>
    <div class="field"><div class="label">Счет / касса</div><div class="line">{{account}}</div></div>
    <div class="field"><div class="label">Комментарий</div><div class="line">{{comment}}</div></div>
    <div class="amount-box">
      <div class="label">Сумма</div>
      <div class="line">{{amountWords}}</div>
      <div class="amount">{{amount}} руб.</div>
    </div>
    <div class="notice">Документ сформирован в PayPlanner. Перед выдачей проверьте реквизиты, сумму и основание операции.</div>
    <div class="signatures">
      <div class="signature">Руководитель<small>подпись, расшифровка</small></div>
      <div class="signature">Главный бухгалтер<small>подпись, расшифровка</small></div>
      <div class="signature">Кассир<small>подпись, расшифровка</small></div>
      <div class="signature">{{signatureLabel}}<small>подпись, расшифровка</small></div>
    </div>

{{receiptHtml}}
  </div>
</body>
</html>
""";
    }

    private static string BuildCashBookPrintHtml(CashBookResponseDto cashBook)
    {
        var rows = cashBook.Days.Count == 0
            ? """
              <tr><td colspan="6" class="empty">За выбранный период движений кассы нет.</td></tr>
              """
            : string.Join(Environment.NewLine, cashBook.Days.Select(day => $$"""
              <tr>
                <td>{{day.Date:dd.MM.yyyy}}</td>
                <td class="num">{{FormatRub(day.OpeningBalance)}}</td>
                <td class="num income">{{FormatRub(day.Income)}}</td>
                <td class="num expense">{{FormatRub(day.Expense)}}</td>
                <td class="num">{{FormatRub(day.ClosingBalance)}}</td>
                <td class="num">ПКО: {{day.IncomingCount}}, РКО: {{day.OutgoingCount}}</td>
              </tr>
              """));

        return $$"""
<!doctype html>
<html lang="ru">
<head>
  <meta charset="utf-8" />
  <title>Кассовая книга {{cashBook.From:dd.MM.yyyy}} - {{cashBook.To:dd.MM.yyyy}}</title>
  {{BuildRegisterPrintStyles()}}
</head>
<body>
  <main class="document">
    <header>
      <div class="eyebrow">Касса</div>
      <h1>Кассовая книга</h1>
      <div class="period">Период: {{cashBook.From:dd.MM.yyyy}} - {{cashBook.To:dd.MM.yyyy}}</div>
    </header>

    <section class="summary">
      <div><span>Остаток на начало</span><strong>{{FormatRub(cashBook.OpeningBalance)}}</strong></div>
      <div><span>Приход</span><strong class="income">{{FormatRub(cashBook.Income)}}</strong><small>ПКО: {{cashBook.IncomingCount}}</small></div>
      <div><span>Расход</span><strong class="expense">{{FormatRub(cashBook.Expense)}}</strong><small>РКО: {{cashBook.OutgoingCount}}</small></div>
      <div><span>Остаток на конец</span><strong>{{FormatRub(cashBook.ClosingBalance)}}</strong></div>
    </section>

    <table>
      <thead>
        <tr>
          <th>Дата</th>
          <th class="num">Остаток на начало</th>
          <th class="num">Приход</th>
          <th class="num">Расход</th>
          <th class="num">Остаток на конец</th>
          <th class="num">Документы</th>
        </tr>
      </thead>
      <tbody>
        {{rows}}
      </tbody>
    </table>

    <footer>
      <div class="signature">Бухгалтер</div>
      <div class="signature">Кассир</div>
    </footer>
  </main>
</body>
</html>
""";
    }

    private static string BuildRegisterPrintHtml(CashOrderRegisterResponseDto register, DateTime? from, DateTime? to)
    {
        var fromText = from?.Date.ToString("dd.MM.yyyy") ?? "не задано";
        var toText = to?.Date.ToString("dd.MM.yyyy") ?? "не задано";
        var rows = register.Items.Count == 0
            ? """
              <tr><td colspan="8" class="empty">За выбранный период кассовых ордеров нет.</td></tr>
              """
            : string.Join(Environment.NewLine, register.Items.Select((order, index) =>
            {
                var typeLabel = order.Type == CashOrderType.Incoming ? "ПКО" : "РКО";
                var statusLabel = order.Status == CashOrderStatus.Cancelled ? "Отменён" : "Проведён";
                var amountClass = order.Type == CashOrderType.Incoming ? "income" : "expense";
                return $$"""
              <tr>
                <td class="num">{{index + 1}}</td>
                <td>{{order.Date:dd.MM.yyyy}}</td>
                <td>{{Html(order.Number)}}</td>
                <td>{{typeLabel}}</td>
                <td>{{statusLabel}}</td>
                <td class="num {{amountClass}}">{{FormatRub(order.Amount)}}</td>
                <td>{{Html(order.PayerName ?? order.ClientName ?? "-")}}</td>
                <td>{{Html(order.Basis ?? "-")}}</td>
              </tr>
              """;
            }));

        return $$"""
<!doctype html>
<html lang="ru">
<head>
  <meta charset="utf-8" />
  <title>Журнал регистрации ПКО/РКО</title>
  {{BuildRegisterPrintStyles()}}
</head>
<body>
  <main class="document">
    <header>
      <div class="eyebrow">Касса</div>
      <h1>Журнал регистрации кассовых документов</h1>
      <div class="period">Период: {{fromText}} - {{toText}}</div>
    </header>

    <section class="summary">
      <div><span>Приходные ордера</span><strong class="income">{{register.IncomingCount}} на {{FormatRub(register.IncomingAmount)}}</strong></div>
      <div><span>Расходные ордера</span><strong class="expense">{{register.OutgoingCount}} на {{FormatRub(register.OutgoingAmount)}}</strong></div>
      <div><span>Проведено</span><strong>{{register.PostedCount}}</strong></div>
      <div><span>Отменено</span><strong>{{register.CancelledCount}}</strong></div>
    </section>

    <table>
      <thead>
        <tr>
          <th class="num">№</th>
          <th>Дата</th>
          <th>Номер</th>
          <th>Тип</th>
          <th>Статус</th>
          <th class="num">Сумма</th>
          <th>Плательщик / получатель</th>
          <th>Основание</th>
        </tr>
      </thead>
      <tbody>
        {{rows}}
      </tbody>
    </table>

    <footer>
      <div class="signature">Бухгалтер</div>
      <div class="signature">Кассир</div>
    </footer>
  </main>
</body>
</html>
""";
    }

    private static string BuildRegisterPrintStyles()
        => """
          <style>
            @page { size: A4 landscape; margin: 14mm; }
            * { box-sizing: border-box; }
            body {
              margin: 0;
              color: #111827;
              font-family: Arial, "DejaVu Sans", sans-serif;
              font-size: 12px;
              line-height: 1.25;
              background: #fff;
              -webkit-print-color-adjust: exact !important;
              print-color-adjust: exact !important;
            }
            .document { max-width: 1180px; margin: 0 auto; }
            header { display: flex; flex-direction: column; gap: 6px; margin-bottom: 18px; }
            .eyebrow { color: #64748b; font-size: 11px; font-weight: 700; letter-spacing: .08em; text-transform: uppercase; }
            h1 { margin: 0; font-size: 24px; }
            .period { color: #475569; font-size: 13px; }
            .summary { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 10px; margin-bottom: 18px; }
            .summary div { border: 1px solid #cbd5e1; border-radius: 6px; padding: 10px; }
            .summary span { display: block; color: #64748b; font-size: 11px; text-transform: uppercase; }
            .summary strong { display: block; margin-top: 5px; font-size: 17px; }
            .summary small { display: block; margin-top: 4px; color: #64748b; }
            table { width: 100%; border-collapse: collapse; table-layout: fixed; font-size: 12px; }
            th, td { border: 1px solid #cbd5e1; padding: 7px 8px; vertical-align: top; word-break: break-word; }
            th { background: #f8fafc; color: #475569; text-align: left; font-size: 11px; text-transform: uppercase; }
            .num { text-align: right; white-space: nowrap; }
            .income { color: #047857; }
            .expense { color: #be123c; }
            .empty { color: #64748b; text-align: center; padding: 22px; }
            footer { display: grid; grid-template-columns: 1fr 1fr; gap: 56px; margin-top: 36px; }
            .signature { border-top: 1px solid #111827; padding-top: 8px; color: #334155; }
          </style>
          """;

    private static string FormatRub(decimal value)
        => $"{value.ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"))} руб.";

    private static string AmountToWordsRu(decimal value)
    {
        var rounded = Math.Round(Math.Abs(value), 2, MidpointRounding.AwayFromZero);
        var rubles = (long)Math.Truncate(rounded);
        var kopecks = (int)((rounded - rubles) * 100m);
        var rublesWords = NumberToWordsRu(rubles, feminine: false);
        var rubleUnit = ChooseRussianPlural(rubles, "рубль", "рубля", "рублей");
        var kopeckUnit = ChooseRussianPlural(kopecks, "копейка", "копейки", "копеек");

        return $"{CapitalizeFirst(rublesWords)} {rubleUnit} {kopecks:00} {kopeckUnit}";
    }

    private static string NumberToWordsRu(long number, bool feminine)
    {
        if (number == 0)
        {
            return "ноль";
        }

        var parts = new List<string>();
        var groups = new[]
        {
            new NumberGroup(1_000_000_000, false, "миллиард", "миллиарда", "миллиардов"),
            new NumberGroup(1_000_000, false, "миллион", "миллиона", "миллионов"),
            new NumberGroup(1_000, true, "тысяча", "тысячи", "тысяч"),
        };

        foreach (var group in groups)
        {
            var value = number / group.Value;
            if (value <= 0)
            {
                continue;
            }

            parts.Add(ThreeDigitsToWords((int)value, group.Feminine));
            parts.Add(ChooseRussianPlural(value, group.One, group.Two, group.Five));
            number %= group.Value;
        }

        if (number > 0)
        {
            parts.Add(ThreeDigitsToWords((int)number, feminine));
        }

        return string.Join(' ', parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string ThreeDigitsToWords(int number, bool feminine)
    {
        var hundreds = new[]
        {
            "", "сто", "двести", "триста", "четыреста", "пятьсот",
            "шестьсот", "семьсот", "восемьсот", "девятьсот",
        };
        var tens = new[]
        {
            "", "", "двадцать", "тридцать", "сорок", "пятьдесят",
            "шестьдесят", "семьдесят", "восемьдесят", "девяносто",
        };
        var teens = new[]
        {
            "десять", "одиннадцать", "двенадцать", "тринадцать", "четырнадцать",
            "пятнадцать", "шестнадцать", "семнадцать", "восемнадцать", "девятнадцать",
        };
        var unitsMasculine = new[]
        {
            "", "один", "два", "три", "четыре", "пять", "шесть", "семь", "восемь", "девять",
        };
        var unitsFeminine = new[]
        {
            "", "одна", "две", "три", "четыре", "пять", "шесть", "семь", "восемь", "девять",
        };

        var parts = new List<string>();
        if (number >= 100)
        {
            parts.Add(hundreds[number / 100]);
        }

        var lastTwo = number % 100;
        if (lastTwo is >= 10 and <= 19)
        {
            parts.Add(teens[lastTwo - 10]);
        }
        else
        {
            if (lastTwo >= 20)
            {
                parts.Add(tens[lastTwo / 10]);
            }

            var unit = lastTwo % 10;
            if (unit > 0)
            {
                parts.Add((feminine ? unitsFeminine : unitsMasculine)[unit]);
            }
        }

        return string.Join(' ', parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string ChooseRussianPlural(long value, string one, string two, string five)
    {
        var lastTwo = value % 100;
        if (lastTwo is >= 11 and <= 14)
        {
            return five;
        }

        return (value % 10) switch
        {
            1 => one,
            >= 2 and <= 4 => two,
            _ => five,
        };
    }

    private static string CapitalizeFirst(string value)
        => string.IsNullOrWhiteSpace(value) ? value : char.ToUpperInvariant(value[0]) + value[1..];

    private sealed record NumberGroup(long Value, bool Feminine, string One, string Two, string Five);

    private static string Html(string value)
        => System.Net.WebUtility.HtmlEncode(value);
}

public sealed class CashOrderCancelRequest
{
    public string? Reason { get; init; }
}
