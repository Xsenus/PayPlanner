using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Banking;

namespace PayPlanner.Api.Controllers;

public sealed record BankAccountDto(
    int Id,
    string ExternalAccountId,
    string AccountNumber,
    string? Bik,
    string Currency,
    string Status,
    bool IsHidden,
    bool IsDefault,
    bool StatementsEnabled,
    DateTime? StatusChangedAt,
    DateTime? StatementWatermark,
    DateTime? LastStatementSyncAt,
    string? LastError);

public sealed record BankConnectionDto(
    int Id,
    int LegalEntityId,
    string Provider,
    string Status,
    string? LastError,
    DateTime? LastAccountsSyncAt,
    IReadOnlyList<BankAccountDto> Accounts);

public sealed record BankOperationDto(
    int Id,
    string ExternalOperationId,
    DateTime OperationDate,
    decimal Amount,
    bool IsIncome,
    bool IsBooked,
    string? CounterpartyInn,
    string? CounterpartyName,
    string? Purpose);

public sealed record BankOperationsPageDto(int Total, int Page, int PageSize, IReadOnlyList<BankOperationDto> Items);

public sealed record BankSyncLogDto(
    int Id,
    int? AccountId,
    string Kind,
    DateTime? PeriodFrom,
    DateTime? PeriodTo,
    string Result,
    int OperationsCount,
    string? Error,
    DateTime CreatedAt);

public sealed record BankSyncLogPageDto(int Total, int Page, int PageSize, IReadOnlyList<BankSyncLogDto> Items);

public sealed class BankStatementPeriodRequest
{
    public Guid RequestId { get; set; }
    public DateTime From { get; set; }
    public DateTime To { get; set; }
}

public sealed class BankAccountSettingsRequest
{
    public bool? IsDefault { get; set; }
    public bool? IsHidden { get; set; }
    public bool? StatementsEnabled { get; set; }
}

public sealed record BankStatementRunItemDto(int AccountId, int Inserted, int Updated, string? Error, int? BatchId = null);

public sealed record BankStatementRunDto(string Status, int Succeeded, int Total, IReadOnlyList<BankStatementRunItemDto> Accounts);

/// <summary>
/// Подключения компаний к банкам: счета, их настройки, загрузка выписок и журнал. Только администратор:
/// распределение прав по ролям (руководитель, бухгалтер, менеджер) ждёт общей настройки прав PAY-18, расширять доступ
/// по предположению нельзя. Токены никогда не возвращаются. Создание подключения (OAuth) появится вместе с адаптерами банков.
/// </summary>
[ApiController]
[Route("api/bank-connections")]
[Authorize(Policy = "Admin")]
public class BankConnectionsController : ControllerBase, IAsyncActionFilter
{
    private const int MaxStatementPeriodDays = 366;
    private const int MaxPageSize = 200;

    private readonly PaymentContext _db;
    private readonly IBankSyncService _sync;

    public BankConnectionsController(PaymentContext db, IBankSyncService sync)
    {
        _db = db;
        _sync = sync;
    }

    [NonAction]
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!await _db.IsApprovedUserAdminAsync(context.HttpContext.User, context.HttpContext.RequestAborted))
        {
            context.Result = Forbid();
            return;
        }

        await next();
    }

    [HttpGet]
    public async Task<ActionResult<List<BankConnectionDto>>> List([FromQuery] int? companyId, CancellationToken ct)
    {
        var query = _db.BankConnections.AsNoTracking().AsQueryable();
        if (companyId.HasValue)
        {
            query = query.Where(item => item.LegalEntityId == companyId.Value);
        }

        var connections = await query.OrderBy(item => item.Id).ToListAsync(ct);
        var ids = connections.Select(item => item.Id).ToList();
        var accounts = await _db.BankConnectionAccounts.AsNoTracking()
            .Where(item => ids.Contains(item.BankConnectionId))
            .OrderBy(item => item.AccountNumber)
            .ToListAsync(ct);
        var byConnection = accounts.GroupBy(item => item.BankConnectionId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<BankAccountDto>)group.Select(MapAccount).ToList());

        var now = DateTime.UtcNow;
        return Ok(connections.Select(item => new BankConnectionDto(
            item.Id,
            item.LegalEntityId,
            item.Provider,
            BankConnectionStatuses.Describe(item.Status, item.RefreshTokenExpiresAt, now),
            item.LastError,
            item.LastAccountsSyncAt,
            byConnection.GetValueOrDefault(item.Id) ?? [])).ToList());
    }

    [HttpPost("{id:int}/sync-accounts")]
    public async Task<ActionResult<BankAccountsSyncResult>> SyncAccounts(int id, CancellationToken ct)
    {
        if (!await _db.BankConnections.AsNoTracking().AnyAsync(item => item.Id == id, ct))
        {
            return NotFound();
        }

        var result = await _sync.SyncAccountsAsync(id, ct);
        return result.Error is null ? Ok(result) : UnprocessableEntity(result);
    }

    [HttpPost("{id:int}/sync-statements")]
    public async Task<ActionResult<BankStatementRunDto>> SyncAllStatements(int id, [FromBody] BankStatementPeriodRequest request, CancellationToken ct)
    {
        if (!await _db.BankConnections.AsNoTracking().AnyAsync(item => item.Id == id, ct))
        {
            return NotFound();
        }

        if (ValidatePeriod(request) is { } error)
        {
            return BadRequest(new { message = error });
        }

        var results = await _sync.SyncAllStatementsForPeriodAsync(id, request.From, request.To, BankSyncInvocation.ForUser(User, request.RequestId), ct);
        return Ok(BuildRun(results));
    }

    [HttpPost("accounts/{accountId:int}/sync-statement")]
    public async Task<ActionResult<BankStatementRunDto>> SyncStatement(int accountId, [FromBody] BankStatementPeriodRequest request, CancellationToken ct)
    {
        if (!await _db.BankConnectionAccounts.AsNoTracking().AnyAsync(item => item.Id == accountId, ct))
        {
            return NotFound();
        }

        if (ValidatePeriod(request) is { } error)
        {
            return BadRequest(new { message = error });
        }

        var result = await _sync.SyncStatementAsync(accountId, request.From, request.To, BankSyncInvocation.ForUser(User, request.RequestId), ct);
        return Ok(BuildRun(new Dictionary<int, BankStatementSyncResult> { [accountId] = result }));
    }

    [HttpPatch("accounts/{accountId:int}")]
    public async Task<ActionResult<BankAccountDto>> UpdateAccount(int accountId, [FromBody] BankAccountSettingsRequest request, CancellationToken ct)
    {
        var result = await _sync.UpdateAccountSettingsAsync(accountId, request.IsDefault, request.IsHidden, request.StatementsEnabled, ct);
        if (!result.Success)
        {
            return result.Error == "Счёт не найден." ? NotFound() : Conflict(new { message = result.Error });
        }

        var account = await _db.BankConnectionAccounts.AsNoTracking().FirstAsync(item => item.Id == accountId, ct);
        return Ok(MapAccount(account));
    }

    [HttpGet("accounts/{accountId:int}/operations")]
    public async Task<ActionResult<BankOperationsPageDto>> GetOperations(
        int accountId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        if (!await _db.BankConnectionAccounts.AsNoTracking().AnyAsync(item => item.Id == accountId, ct))
        {
            return NotFound();
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var query = _db.BankStatementOperations.AsNoTracking().Where(item => item.BankConnectionAccountId == accountId);
        if (from.HasValue)
        {
            query = query.Where(item => item.OperationDate >= from.Value);
        }

        if (to.HasValue)
        {
            query = query.Where(item => item.OperationDate <= to.Value);
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(item => item.OperationDate)
            .ThenByDescending(item => item.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(item => new BankOperationDto(
                item.Id, item.ExternalOperationId, item.OperationDate, item.Amount, item.IsIncome, item.IsBooked,
                item.CounterpartyInn, item.CounterpartyName, item.Purpose))
            .ToListAsync(ct);
        return Ok(new BankOperationsPageDto(total, page, pageSize, items));
    }

    [HttpGet("{id:int}/logs")]
    public async Task<ActionResult<BankSyncLogPageDto>> GetLogs(int id, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        if (!await _db.BankConnections.AsNoTracking().AnyAsync(item => item.Id == id, ct))
        {
            return NotFound();
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var query = _db.BankSyncLogs.AsNoTracking().Where(item => item.BankConnectionId == id);
        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(item => new BankSyncLogDto(
                item.Id, item.BankConnectionAccountId, item.Kind, item.PeriodFrom, item.PeriodTo, item.Result,
                item.OperationsCount, item.Error, item.CreatedAt))
            .ToListAsync(ct);
        return Ok(new BankSyncLogPageDto(total, page, pageSize, items));
    }

    private static string? ValidatePeriod(BankStatementPeriodRequest request)
    {
        if (request.RequestId == Guid.Empty) return "Укажите идентификатор запроса синхронизации.";
        if (request.From == default || request.To == default || request.From > request.To)
        {
            return "Укажите корректный период: дата начала не позже даты окончания.";
        }

        return (request.To - request.From).TotalDays > MaxStatementPeriodDays
            ? $"Период выписки не может превышать {MaxStatementPeriodDays} дней за один запрос."
            : null;
    }

    private static BankStatementRunDto BuildRun(IReadOnlyDictionary<int, BankStatementSyncResult> results)
    {
        var items = results
            .OrderBy(item => item.Key)
            .Select(item => new BankStatementRunItemDto(item.Key, item.Value.Inserted, item.Value.Updated, item.Value.Error, item.Value.BatchId))
            .ToList();
        var succeeded = items.Count(item => item.Error is null);
        // Итог по ТЗ: «Успешно / Частично (N из M) / Ошибка».
        var status = items.Count == 0 || succeeded == items.Count ? "Success" : succeeded == 0 ? "Error" : "Partial";
        return new BankStatementRunDto(status, succeeded, items.Count, items);
    }

    private static BankAccountDto MapAccount(BankConnectionAccount item)
        => new(item.Id, item.ExternalAccountId, item.AccountNumber, item.Bik, item.Currency, item.Status, item.IsHidden,
            item.IsDefault, item.StatementsEnabled, item.StatusChangedAt, item.StatementWatermark, item.LastStatementSyncAt, item.LastError);
}
