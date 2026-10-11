using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Tax;

namespace PayPlanner.Api.Controllers;

public sealed record TaxRegisterEntryDto(
    int Id,
    int LegalEntityId,
    DateTime OperationDate,
    string Type,
    string Direction,
    decimal Amount,
    int Sign,
    string Status,
    string? DocumentRef,
    string? Description,
    string? Category,
    int? BankStatementOperationId,
    int? CorrectsEntryId,
    int? CorrectedByEntryId,
    string? CorrectionReason,
    DateTime? ApprovedAt);

public sealed record TaxRegisterEventDto(int Id, string Action, string? FromStatus, string? ToStatus, int UserId, string? Comment, DateTime CreatedAt);

public sealed record TaxRegisterBookDto(int LegalEntityId, int Year, bool IsClosed, TaxRegisterBookTotals Totals, IReadOnlyList<TaxRegisterEntryDto> Entries, int Page, int PageSize);

public sealed class TaxRegisterEntryRequest
{
    public int LegalEntityId { get; set; }
    public string? Type { get; set; }
    public DateTime? OperationDate { get; set; }
    public decimal? Amount { get; set; }
    public string? DocumentRef { get; set; }
    public int? DocumentId { get; set; }
    public string? Description { get; set; }
    public string? Category { get; set; }
    public string? SubType { get; set; }
    public bool TaxBaseIncludesVat { get; set; }
    public int? BankStatementOperationId { get; set; }
}

public sealed class TaxRegisterReasonRequest
{
    public string? Reason { get; set; }
}

/// <summary>
/// Регистр книги доходов и расходов. Только администратор: роли «бухгалтер» и «руководитель» подключатся вместе
/// с общей настройкой прав (PAY-18), доступ по предположению не расширяется.
/// </summary>
[ApiController]
[Route("api/tax-register")]
[Authorize(Policy = "Admin")]
public class TaxRegisterController : ControllerBase, IAsyncActionFilter
{
    private const int MaxPageSize = 200;

    private readonly PaymentContext _db;
    private readonly ITaxRegisterService _service;

    public TaxRegisterController(PaymentContext db, ITaxRegisterService service)
    {
        _db = db;
        _service = service;
    }

    [NonAction]
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!await _db.IsUserAdminOrLegacyFallbackAsync(context.HttpContext.User, context.HttpContext.RequestAborted))
        {
            context.Result = Forbid();
            return;
        }

        await next();
    }

    [HttpGet("entries")]
    public async Task<ActionResult<object>> List(
        [FromQuery] int companyId, [FromQuery] int? year, [FromQuery] string? status, [FromQuery] string? type,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        if (year is < 2000 or > 2100 || (long)(page - 1) * pageSize > int.MaxValue)
            return BadRequest(new { message = "Укажите корректный год и страницу." });
        var query = _db.TaxRegisterEntries.AsNoTracking().Where(item => item.LegalEntityId == companyId);
        if (year.HasValue)
        {
            var from = new DateTime(year.Value, 1, 1);
            var to = from.AddYears(1);
            query = query.Where(item => item.OperationDate >= from && item.OperationDate < to);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(item => item.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(type))
        {
            query = query.Where(item => item.Type == type);
        }

        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(item => item.OperationDate).ThenByDescending(item => item.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return Ok(new { total, page, pageSize, items = items.Select(Map).ToList() });
    }

    [HttpPost("entries")]
    public async Task<ActionResult<TaxRegisterEntryDto>> Create([FromBody] TaxRegisterEntryRequest request, CancellationToken ct)
        => ToResult(await _service.CreateAsync(ToInput(request), User.GetUserId() ?? 0, ct));

    [HttpPut("entries/{id:int}")]
    public async Task<ActionResult<TaxRegisterEntryDto>> Update(int id, [FromBody] TaxRegisterEntryRequest request, CancellationToken ct)
        => ToResult(await _service.UpdateDraftAsync(id, ToInput(request), User.GetUserId() ?? 0, ct));

    [HttpPost("entries/{id:int}/approve")]
    public async Task<ActionResult<TaxRegisterEntryDto>> Approve(int id, CancellationToken ct)
        => ToResult(await _service.ApproveAsync(id, User.GetUserId() ?? 0, ct));

    [HttpPost("entries/{id:int}/archive")]
    public async Task<ActionResult<TaxRegisterEntryDto>> Archive(int id, [FromBody] TaxRegisterReasonRequest? request, CancellationToken ct)
        => ToResult(await _service.ArchiveAsync(id, User.GetUserId() ?? 0, request?.Reason, ct));

    [HttpPost("entries/{id:int}/correct")]
    public async Task<ActionResult<TaxRegisterEntryDto>> Correct(int id, [FromBody] TaxRegisterReasonRequest request, CancellationToken ct)
        => ToResult(await _service.CorrectAsync(id, request?.Reason ?? string.Empty, User.GetUserId() ?? 0, ct));

    [HttpGet("entries/{id:int}/events")]
    public async Task<ActionResult<List<TaxRegisterEventDto>>> Events(int id, CancellationToken ct)
    {
        if (!await _db.TaxRegisterEntries.AsNoTracking().AnyAsync(item => item.Id == id, ct))
        {
            return NotFound();
        }

        var events = await _db.TaxRegisterEvents.AsNoTracking()
            .Where(item => item.TaxRegisterEntryId == id)
            .OrderBy(item => item.CreatedAt).ThenBy(item => item.Id)
            .ToListAsync(ct);
        return Ok(events.Select(item => new TaxRegisterEventDto(item.Id, item.Action, item.FromStatus, item.ToStatus, item.UserId, item.Comment, item.CreatedAt)).ToList());
    }

    [HttpGet("journal")]
    public async Task<ActionResult<TaxRegisterJournalPage>> Journal(
        [FromQuery] int companyId, [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] string? action,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        if ((long)(page - 1) * pageSize > int.MaxValue || to?.Date == DateTime.MaxValue.Date || (from.HasValue && to.HasValue && from.Value.Date > to.Value.Date))
            return BadRequest(new { message = "Укажите корректный период и страницу." });
        return Ok(await _service.GetJournalAsync(companyId, from, to, action, page, pageSize, ct));
    }

    [HttpGet("book")]
    public async Task<ActionResult<TaxRegisterBookDto>> Book(
        [FromQuery] int companyId, [FromQuery] int year, [FromQuery] int page = 1, [FromQuery] int pageSize = 100, CancellationToken ct = default)
    {
        if (year is < 2000 or > 2100)
        {
            return BadRequest(new { message = "Укажите корректный год." });
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        if ((long)(page - 1) * pageSize > int.MaxValue)
        {
            return BadRequest(new { message = "Укажите корректную страницу." });
        }

        var book = await _service.GetBookPageAsync(companyId, year, page, pageSize, ct);
        return Ok(new TaxRegisterBookDto(book.LegalEntityId, book.Year, book.IsClosed, book.Totals, book.Entries.Select(Map).ToList(), book.Page, book.PageSize));
    }

    [HttpGet("book/export.csv")]
    public async Task<IActionResult> ExportCsv([FromQuery] int companyId, [FromQuery] int year, CancellationToken ct)
    {
        if (year is < 2000 or > 2100)
        {
            return BadRequest(new { message = "Укажите корректный год." });
        }

        // Заголовки фиксируются до первой записи в поток; тело пишется партиями без накопления года в памяти.
        Response.ContentType = "text/csv; charset=utf-8";
        Response.Headers.ContentDisposition = $"attachment; filename=\"kudir-{companyId}-{year}.csv\"";
        await TaxRegisterExport.WriteCsvAsync(Response.Body, _service.StreamBookEntriesAsync(companyId, year, ct), ct);
        return new EmptyResult();
    }

    [HttpGet("book/export.xlsx")]
    public async Task<IActionResult> ExportXlsx([FromQuery] int companyId, [FromQuery] int year, CancellationToken ct)
    {
        if (year is < 2000 or > 2100)
        {
            return BadRequest(new { message = "Укажите корректный год." });
        }

        Response.ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
        Response.Headers.ContentDisposition = $"attachment; filename=\"kudir-{companyId}-{year}.xlsx\"";
        await TaxRegisterExport.WriteXlsxAsync(Response.Body, year, _service.StreamBookEntriesAsync(companyId, year, ct), ct);
        return new EmptyResult();
    }

    [HttpPost("close-year")]
    public async Task<ActionResult<object>> CloseYear([FromQuery] int companyId, [FromQuery] int year, CancellationToken ct)
    {
        var result = await _service.CloseYearAsync(companyId, year, User.GetUserId() ?? 0, ct);
        return result.Ok
            ? Ok(new { result.Value!.Year, result.Value.ClosedAt, result.Value.DataHash })
            : StatusCode(result.Status, new { message = result.Error });
    }

    private ActionResult<TaxRegisterEntryDto> ToResult(TaxRegisterResult<TaxRegisterEntry> result)
        => result.Ok ? Ok(Map(result.Value!)) : StatusCode(result.Status, new { message = result.Error });

    private static TaxRegisterEntryInput ToInput(TaxRegisterEntryRequest request) => new(
        request.LegalEntityId, request.Type, request.OperationDate, request.Amount, request.DocumentRef, request.DocumentId,
        request.Description, request.Category, request.SubType, request.TaxBaseIncludesVat, request.BankStatementOperationId);

    private static TaxRegisterEntryDto Map(TaxRegisterEntry item) => new(
        item.Id, item.LegalEntityId, item.OperationDate, item.Type, item.Direction, item.Amount, item.Sign, item.Status,
        item.DocumentRef, item.Description, item.Category, item.BankStatementOperationId, item.CorrectsEntryId,
        item.CorrectedByEntryId, item.CorrectionReason, item.ApprovedAt);
}
