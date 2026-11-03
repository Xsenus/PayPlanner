using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Banking;
using PayPlanner.Api.Services.Invoices;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/cash-orders")]
[Authorize]
public class CashOrdersController : ControllerBase
{
    private const string CurrencyContractHeader = "X-PayPlanner-Currency-Contract";
    private const string CashSourceToken = "налич";
    private const string CashRegisterSourceToken = "кас";
    private readonly PaymentContext _db;
    private readonly IInvoiceDocumentPdfService? _pdfService;
    private readonly IConfiguration? _configuration;
    private BankStatementImportAccessContext? _companyAccess;

    public CashOrdersController(
        PaymentContext db,
        IInvoiceDocumentPdfService? pdfService = null,
        IConfiguration? configuration = null)
    {
        _db = db;
        _pdfService = pdfService;
        _configuration = configuration;
    }

    private async Task<ForbidResult?> RequirePaymentsPermissionAsync(
        Func<RolePermissionValues, bool> isAllowed,
        CancellationToken ct)
        => await _db.HasUserSectionPermissionAsync(User, RolePermissionCatalog.Payments, isAllowed, ct)
            ? null
            : Forbid();

    private async Task<DataVisibilityAccess?> ResolvePaymentsVisibilityAsync(CancellationToken ct)
    {
        _companyAccess = await BankStatementImportAccess.ResolveAsync(_db, User, ct);
        return _companyAccess?.Visibility;
    }

    private IQueryable<CashOrder> CompanyCashOrders
        => BankStatementImportAccess.ScopeCashOrderCompanies(_db, _db.CashOrders, _companyAccess);

    private IQueryable<Payment> CompanyPayments
        => _companyAccess is null ? _db.Payments.Where(_ => false)
            : _companyAccess.Visibility.IsAdmin ? _db.Payments
            : BankStatementImportAccess.ScopePaymentCompanies(_db, _db.Payments, _companyAccess.AllowedCompanyIds);

    private async Task<bool> CanViewSelectedCashAccountAsync(int? accountId, CancellationToken ct)
        => !accountId.HasValue || _companyAccess is not null && await BankStatementImportAccess.CoherentAccounts(_db,
            BankStatementImportAccess.ScopeAccounts(_db.MoneyAccounts.AsNoTracking(), _companyAccess, ignoreQueryFilters: false))
            .AnyAsync(account => account.Id == accountId.Value && account.Source == MoneyOperationSource.Cash, ct);

    private bool SupportsCurrencyContract()
        => string.Equals(Request.Headers[CurrencyContractHeader].ToString(), "1", StringComparison.Ordinal);

    private void MarkCurrencyResponse()
    {
        Response.Headers.Append("Vary", CurrencyContractHeader);
        Response.Headers.CacheControl = "no-store";
    }

    [HttpGet("cashbook")]
    public async Task<ActionResult<CashBookResponseDto>> GetCashBook(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? accountName = null,
        [FromQuery] string? counterpartyName = null,
        [FromQuery] int? clientId = null,
        [FromQuery] int? caseId = null,
        [FromQuery] int? contractId = null,
        [FromQuery] int? incomeTypeId = null,
        CancellationToken ct = default,
        [FromQuery] int? moneyAccountId = null)
    {
        if (await RequirePaymentsPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolvePaymentsVisibilityAsync(ct);
        if (visibility is null)
        {
            return Forbid();
        }
        if (!await CanViewSelectedCashAccountAsync(moneyAccountId, ct)) return NotFound();

        var cashBook = await BuildCashBookAsync(
            from,
            to,
            accountName,
            counterpartyName,
            clientId,
            caseId,
            contractId,
            incomeTypeId,
            visibility,
            ct,
            moneyAccountId);
        MarkCurrencyResponse();
        if (cashBook is not null && cashBook.CurrencyStatus == "single" && cashBook.Currency != "RUB" && !SupportsCurrencyContract())
            return Conflict("Обновите страницу, чтобы безопасно отобразить кассовую книгу в выбранной валюте.");
        return cashBook is null
            ? BadRequest("Нельзя рассчитать кассовую книгу: валюты проведённых ордеров различаются или не указаны.")
            : Ok(cashBook);
    }

    [HttpGet("cashbook/print")]
    public async Task<IActionResult> PrintCashBook(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? accountName = null,
        [FromQuery] string? counterpartyName = null,
        [FromQuery] int? clientId = null,
        [FromQuery] int? caseId = null,
        [FromQuery] int? contractId = null,
        [FromQuery] int? incomeTypeId = null,
        CancellationToken ct = default,
        [FromQuery] int? moneyAccountId = null)
    {
        if (await RequirePaymentsPermissionAsync(static permission => permission.CanExport, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolvePaymentsVisibilityAsync(ct);
        if (visibility is null)
        {
            return Forbid();
        }
        if (!await CanViewSelectedCashAccountAsync(moneyAccountId, ct)) return NotFound();

        var cashBook = await BuildCashBookAsync(
            from,
            to,
            accountName,
            counterpartyName,
            clientId,
            caseId,
            contractId,
            incomeTypeId,
            visibility,
            ct,
            moneyAccountId);
        return cashBook is null
            ? BadRequest("Нельзя распечатать кассовую книгу: валюты проведённых ордеров различаются или не указаны.")
            : Content(BuildCashBookPrintHtml(cashBook), "text/html; charset=utf-8", Encoding.UTF8);
    }

    [HttpGet("next-number")]
    public async Task<IActionResult> GetNextNumber(
        [FromQuery] CashOrderType type,
        [FromQuery] DateTime? date,
        CancellationToken ct)
    {
        if (await RequirePaymentsPermissionAsync(static permission => permission.CanCreate, ct) is { } forbidden)
        {
            return forbidden;
        }

        var today = await ResolveCashOrderTodayAsync(ct);
        var effectiveDate = (date ?? today).Date;
        var number = await GenerateNumberAsync(type, effectiveDate, ct);
        return Ok(new
        {
            number,
            type,
            date = effectiveDate,
        });
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
        [FromQuery] string? accountName = null,
        CancellationToken ct = default,
        [FromQuery] int? moneyAccountId = null)
    {
        if (await RequirePaymentsPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 5, 200);

        var visibility = await ResolvePaymentsVisibilityAsync(ct);
        if (visibility is null)
        {
            return Forbid();
        }
        if (!await CanViewSelectedCashAccountAsync(moneyAccountId, ct)) return NotFound();

        var query = BuildFilteredQuery(from, to, type, status, search, accountName, visibility, moneyAccountId);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(order => order.Date)
            .ThenByDescending(order => order.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(CashOrderListProjection)
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
        [FromQuery] string? accountName = null,
        CancellationToken ct = default,
        [FromQuery] int? moneyAccountId = null)
    {
        if (await RequirePaymentsPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 5, 200);

        var visibility = await ResolvePaymentsVisibilityAsync(ct);
        if (visibility is null)
        {
            return Forbid();
        }
        if (!await CanViewSelectedCashAccountAsync(moneyAccountId, ct)) return NotFound();

        var query = BuildFilteredQuery(from, to, type, status, search, accountName, visibility, moneyAccountId);
        var summary = await query
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Total = group.Count(),
                IncomingCount = group.Count(order => order.Type == CashOrderType.Incoming),
                OutgoingCount = group.Count(order => order.Type == CashOrderType.Outgoing),
                DraftCount = group.Count(order => order.Status == CashOrderStatus.Draft),
                PostedCount = group.Count(order => order.Status == CashOrderStatus.Posted),
                CancelledCount = group.Count(order => order.Status == CashOrderStatus.Cancelled),
                HasUnknownCurrency = group.Any(order => order.Status == CashOrderStatus.Posted
                    && (order.Currency == null || order.Currency.Trim() == "")),
                HasLegacyUnsafeRows = group.Any(order => order.Currency == null || order.Currency.Trim().ToUpper() != "RUB"),
                MinCurrency = group.Where(order => order.Status == CashOrderStatus.Posted)
                    .Min(order => order.Currency == null ? null : order.Currency.Trim().ToUpper()),
                MaxCurrency = group.Where(order => order.Status == CashOrderStatus.Posted)
                    .Max(order => order.Currency == null ? null : order.Currency.Trim().ToUpper()),
                IncomingAmount = group
                    .Where(order => order.Type == CashOrderType.Incoming && order.Status == CashOrderStatus.Posted)
                    .Sum(order => order.Amount),
                OutgoingAmount = group
                    .Where(order => order.Type == CashOrderType.Outgoing && order.Status == CashOrderStatus.Posted)
                    .Sum(order => order.Amount),
            })
            .FirstOrDefaultAsync(ct);

        MarkCurrencyResponse();
        if (summary?.HasLegacyUnsafeRows == true && !SupportsCurrencyContract())
            return Conflict("Обновите страницу, чтобы безопасно отобразить валюту кассовых ордеров.");

        var items = await query
            .OrderByDescending(order => order.Date)
            .ThenByDescending(order => order.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(CashOrderListProjection)
            .ToListAsync(ct);

        var (currencyStatus, currency) = ResolveRegisterCurrency(summary?.PostedCount ?? 0,
            summary?.HasUnknownCurrency == true, summary?.MinCurrency, summary?.MaxCurrency);
        var amountsAvailable = currencyStatus is "single" or "empty";
        return Ok(new CashOrderRegisterResponseDto
        {
            Items = items,
            MoneyAccountId = moneyAccountId,
            Total = summary?.Total ?? 0,
            Page = page,
            PageSize = pageSize,
            IncomingCount = summary?.IncomingCount ?? 0,
            OutgoingCount = summary?.OutgoingCount ?? 0,
            DraftCount = summary?.DraftCount ?? 0,
            PostedCount = summary?.PostedCount ?? 0,
            CancelledCount = summary?.CancelledCount ?? 0,
            AmountsAvailable = amountsAvailable,
            CurrencyStatus = currencyStatus,
            Currency = currency,
            IncomingAmount = amountsAvailable ? summary?.IncomingAmount ?? 0m : null,
            OutgoingAmount = amountsAvailable ? summary?.OutgoingAmount ?? 0m : null,
        });
    }

    [HttpGet("register/print")]
    public async Task<IActionResult> PrintRegister(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] CashOrderType? type,
        [FromQuery] CashOrderStatus? status,
        [FromQuery] string? search,
        [FromQuery] string? accountName = null,
        CancellationToken ct = default,
        [FromQuery] int? moneyAccountId = null)
    {
        if (await RequirePaymentsPermissionAsync(static permission => permission.CanExport, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolvePaymentsVisibilityAsync(ct);
        if (visibility is null)
        {
            return Forbid();
        }
        if (!await CanViewSelectedCashAccountAsync(moneyAccountId, ct)) return NotFound();

        var query = BuildFilteredQuery(from, to, type, status, search, accountName, visibility, moneyAccountId);
        var summary = await query
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Total = group.Count(),
                IncomingCount = group.Count(order => order.Type == CashOrderType.Incoming),
                OutgoingCount = group.Count(order => order.Type == CashOrderType.Outgoing),
                DraftCount = group.Count(order => order.Status == CashOrderStatus.Draft),
                PostedCount = group.Count(order => order.Status == CashOrderStatus.Posted),
                CancelledCount = group.Count(order => order.Status == CashOrderStatus.Cancelled),
                HasUnknownCurrency = group.Any(order => order.Status == CashOrderStatus.Posted
                    && (order.Currency == null || order.Currency.Trim() == "")),
                MinCurrency = group.Where(order => order.Status == CashOrderStatus.Posted)
                    .Min(order => order.Currency == null ? null : order.Currency.Trim().ToUpper()),
                MaxCurrency = group.Where(order => order.Status == CashOrderStatus.Posted)
                    .Max(order => order.Currency == null ? null : order.Currency.Trim().ToUpper()),
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
            .Select(CashOrderListProjection)
            .ToListAsync(ct);

        var (currencyStatus, currency) = ResolveRegisterCurrency(summary?.PostedCount ?? 0,
            summary?.HasUnknownCurrency == true, summary?.MinCurrency, summary?.MaxCurrency);
        var amountsAvailable = currencyStatus is "single" or "empty";
        var register = new CashOrderRegisterResponseDto
        {
            Items = items,
            MoneyAccountId = moneyAccountId,
            Total = summary?.Total ?? 0,
            Page = 1,
            PageSize = items.Count,
            IncomingCount = summary?.IncomingCount ?? 0,
            OutgoingCount = summary?.OutgoingCount ?? 0,
            DraftCount = summary?.DraftCount ?? 0,
            PostedCount = summary?.PostedCount ?? 0,
            CancelledCount = summary?.CancelledCount ?? 0,
            AmountsAvailable = amountsAvailable,
            CurrencyStatus = currencyStatus,
            Currency = currency,
            IncomingAmount = amountsAvailable ? summary?.IncomingAmount ?? 0m : null,
            OutgoingAmount = amountsAvailable ? summary?.OutgoingAmount ?? 0m : null,
        };

        return Content(BuildRegisterPrintHtml(register, from, to, accountName), "text/html; charset=utf-8", Encoding.UTF8);
    }

    private static (string Status, string? Currency) ResolveRegisterCurrency(int postedCount, bool unknown, string? min, string? max)
        => postedCount == 0 ? ("empty", null)
            : unknown || string.IsNullOrWhiteSpace(min) || string.IsNullOrWhiteSpace(max) ? ("unknown", null)
            : !string.Equals(min, max, StringComparison.Ordinal) ? ("mixed", null)
            : ("single", min);

    internal static (string Status, string? Currency) ResolveCashBookCurrency(IEnumerable<(bool Unknown, string? Min, string? Max)> groups)
    {
        string? currency = null;
        foreach (var group in groups)
        {
            if (group.Unknown || string.IsNullOrWhiteSpace(group.Min) || string.IsNullOrWhiteSpace(group.Max)) return ("unknown", null);
            if (!string.Equals(group.Min, group.Max, StringComparison.Ordinal)
                || currency is not null && !string.Equals(currency, group.Min, StringComparison.Ordinal)) return ("mixed", null);
            currency = group.Min;
        }
        return currency is null ? ("empty", null) : ("single", currency);
    }

    [HttpGet("inventories")]
    public async Task<ActionResult<CashInventoryPageResponse>> GetInventories(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? accountName = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] int? moneyAccountId = null,
        CancellationToken ct = default)
    {
        if (await RequirePaymentsPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolvePaymentsVisibilityAsync(ct);
        if (visibility is null || _companyAccess is null)
        {
            return Forbid();
        }
        if (!await CanViewSelectedCashAccountAsync(moneyAccountId, ct)) return NotFound();

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 5, 100);

        var query = _db.CashInventories
            .Include(item => item.CorrectionCashOrder)
            .AsNoTracking()
            .AsQueryable();

        if (!visibility.IsAdmin)
        {
            var accounts = BankStatementImportAccess.CoherentAccounts(_db,
                BankStatementImportAccess.ScopeAccounts(_db.MoneyAccounts.AsNoTracking(), _companyAccess, ignoreQueryFilters: false));
            var corrections = CompanyCashOrders.AsNoTracking().ApplyCashOrderVisibility(visibility);
            query = query.Where(item => item.CompanyId.HasValue && item.MoneyAccountId.HasValue
                && accounts.Any(account => account.Id == item.MoneyAccountId && account.CompanyId == item.CompanyId)
                && (!item.CorrectionCashOrderId.HasValue || corrections.Any(order => order.Id == item.CorrectionCashOrderId)));
            if (!visibility.SeesAll)
            {
                var visibleUserIds = visibility.VisibleUserIds.ToArray();
                query = query.Where(item => EF.Property<int?>(item, "CreatedByUserId").HasValue
                    && visibleUserIds.Contains(EF.Property<int?>(item, "CreatedByUserId")!.Value));
            }
        }

        if (from.HasValue)
        {
            query = query.Where(item => item.Date >= from.Value.Date);
        }

        if (to.HasValue)
        {
            query = query.Where(item => item.Date <= to.Value.Date);
        }

        var accountFilter = NormalizeAccountFilter(accountName);
        if (moneyAccountId.HasValue)
        {
            query = query.Where(item => item.MoneyAccountId == moneyAccountId.Value);
        }
        else if (accountFilter is not null)
        {
            query = query.Where(item => item.AccountName == accountFilter);
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(item => item.Date)
            .ThenByDescending(item => item.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(item => Map(item))
            .ToListAsync(ct);

        return Ok(new CashInventoryPageResponse
        {
            Items = items,
            MoneyAccountId = moneyAccountId,
            Total = total,
            Page = page,
            PageSize = pageSize,
        });
    }

    [HttpPost("inventories")]
    public async Task<IActionResult> CreateInventory([FromBody] CashInventoryCreateRequest request, CancellationToken ct)
    {
        try
        {
            return await CreateInventoryCoreAsync(request, ct);
        }
        catch (Exception error) when (_db.Database.CurrentTransaction is null && IsInventoryConcurrencyConflict(error))
        {
            return Conflict("Касса или её остаток изменились одновременно с инвентаризацией. Обновите данные и повторите действие.");
        }
    }

    private static bool IsInventoryConcurrencyConflict(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is DbUpdateConcurrencyException
                || current is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 5 or 6 }
                || current is Npgsql.PostgresException
                {
                    SqlState: Npgsql.PostgresErrorCodes.SerializationFailure
                    or Npgsql.PostgresErrorCodes.DeadlockDetected or Npgsql.PostgresErrorCodes.LockNotAvailable
                }) return true;
        return false;
    }

    private async Task<IActionResult> CreateInventoryCoreAsync(CashInventoryCreateRequest request, CancellationToken ct)
    {
        if (await RequirePaymentsPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var accountName = NormalizeAccountFilter(request.AccountName);
        if (accountName is null)
        {
            return BadRequest("Укажите кассу для инвентаризации.");
        }

        if (request.ActualBalance < 0m)
        {
            return BadRequest("Фактический остаток кассы не может быть отрицательным.");
        }

        var today = await ResolveCashOrderTodayAsync(ct);
        var date = request.Date == default ? today : request.Date.Date;
        var visibility = await ResolvePaymentsVisibilityAsync(ct);
        if (visibility is null)
        {
            return Forbid();
        }

        await using var transaction = _db.Database.CurrentTransaction is null
            ? await _db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct)
            : null;
        // With no explicit identity, an old client must not guess between accounts.
        // Unassigned orders with the same name are rejected below in both paths.
        var matchingAccounts = await _db.MoneyAccounts.IgnoreQueryFilters().AsNoTracking()
            .Where(account => account.Source == MoneyOperationSource.Cash && account.Name == accountName
                && (!request.MoneyAccountId.HasValue || account.Id == request.MoneyAccountId.Value))
            .Select(account => new { account.Id, account.CompanyId, account.Currency }).Take(2).ToListAsync(ct);
        if (matchingAccounts.Count != 1 || request.MoneyAccountId.HasValue && request.MoneyAccountId.Value != matchingAccounts[0].Id
            || !string.Equals(matchingAccounts[0].Currency?.Trim(), "RUB", StringComparison.OrdinalIgnoreCase)
            || _companyAccess is null || !await BankStatementImportAccess.CoherentAccounts(
                    _db, BankStatementImportAccess.ScopeAccounts(_db.MoneyAccounts.AsNoTracking(), _companyAccess,
                        ignoreQueryFilters: false), forNewOperations: true)
                .AnyAsync(account => account.Id == matchingAccounts[0].Id, ct))
        {
            return BadRequest("Не удалось подтвердить остаток кассы. Проверьте настройки и доступ к операциям.");
        }

        var selected = matchingAccounts[0];
        // Use the same company -> cash account lock order as cash-order mutations.
        if (await _db.LegalEntities.Where(company => company.Id == selected.CompanyId && company.ProfileStatus == CompanyProfileStatuses.Active)
                .ExecuteUpdateAsync(setters => setters.SetProperty(company => company.ShortName, company => company.ShortName), ct) != 1
            || await _db.MoneyAccounts.Where(account => account.Id == selected.Id && account.IsActive
                && account.CompanyId == selected.CompanyId && account.Source == MoneyOperationSource.Cash
                && account.Currency == selected.Currency && account.Name == accountName)
                .ExecuteUpdateAsync(setters => setters.SetProperty(account => account.Name, account => account.Name), ct) != 1)
            return Conflict("Касса изменилась. Обновите данные и повторите инвентаризацию.");
        if (await RequirePaymentsPermissionAsync(static permission => permission.CanEdit, ct) is { } changedPermission) return changedPermission;
        var freshVisibility = await ResolvePaymentsVisibilityAsync(ct);
        if (freshVisibility is null || _companyAccess is null
            || !await BankStatementImportAccess.CoherentAccounts(_db,
                BankStatementImportAccess.ScopeAccounts(_db.MoneyAccounts.AsNoTracking(), _companyAccess, ignoreQueryFilters: false),
                forNewOperations: true).AnyAsync(account => account.Id == selected.Id && account.CompanyId == selected.CompanyId, ct))
            return Forbid();
        visibility = freshVisibility;

        var selectedAccountId = matchingAccounts[0].Id;
        var selectedCompanyId = matchingAccounts[0].CompanyId;
        System.Linq.Expressions.Expression<Func<CashOrder, bool>> inventoryOrder = order =>
            order.Status == CashOrderStatus.Posted && order.Date <= date.Date
            && (order.MoneyAccountId == selectedAccountId || !order.MoneyAccountId.HasValue && order.Account == accountName);
        var relevantOrders = _db.CashOrders.AsNoTracking().Where(inventoryOrder);
        var visibleOrders = CompanyCashOrders.AsNoTracking().ApplyCashOrderVisibility(visibility).Where(inventoryOrder);
        // A partial balance must never be used to create a correction. Do not reveal
        // which inaccessible order caused the rejection.
        if (await relevantOrders.CountAsync(ct) != await visibleOrders.CountAsync(ct)
            || await visibleOrders.AnyAsync(order => order.Currency == null || order.Currency.Trim().ToUpper() != "RUB", ct)
            || await relevantOrders.AnyAsync(order => !order.MoneyAccountId.HasValue
                || order.MoneyAccountId == selectedAccountId && !order.CompanyId.HasValue
                || order.CompanyId.HasValue && order.CompanyId != selectedCompanyId
                || order.MoneyOperationId.HasValue
                && _db.MoneyOperations.IgnoreQueryFilters().Any(operation => operation.Id == order.MoneyOperationId.Value
                    && operation.MoneyAccountId.HasValue && operation.MoneyAccountId.Value != matchingAccounts[0].Id)
                || !order.MoneyOperationId.HasValue && order.PaymentId.HasValue
                && _db.Payments.IgnoreQueryFilters().Any(payment => payment.Id == order.PaymentId.Value
                    && payment.SellerCompanyId.HasValue && payment.SellerCompanyId != matchingAccounts[0].CompanyId), ct))
        {
            return BadRequest("Не удалось подтвердить остаток кассы. Проверьте настройки и доступ к операциям.");
        }

        var accountingBalance = await visibleOrders.SumAsync(
            order => order.Type == CashOrderType.Incoming ? order.Amount : -order.Amount, ct);
        var difference = request.ActualBalance - accountingBalance;

        CashOrder? correctionOrder = null;
        if (request.CreateCorrection && difference != 0m)
        {
            var correctionType = difference > 0m ? CashOrderType.Incoming : CashOrderType.Outgoing;
            correctionOrder = new CashOrder
            {
                Number = await GenerateNumberAsync(correctionType, date, ct),
                Type = correctionType,
                Status = CashOrderStatus.Posted,
                Date = date,
                Amount = Math.Abs(difference),
                Currency = "RUB",
                Account = accountName,
                MoneyAccountId = matchingAccounts[0].Id,
                CompanyId = matchingAccounts[0].CompanyId,
                PayerName = difference > 0m ? "Инвентаризация кассы" : null,
                ReceiverName = difference < 0m ? "Инвентаризация кассы" : null,
                Basis = "Корректировка по инвентаризации кассы",
                Comment = Normalize(request.Comment),
                CreatedAt = DateTime.UtcNow,
                CreatedByUserId = User.GetUserId(),
                CreatedByUserName = User.GetDisplayName(),
            };
            _db.CashOrders.Add(correctionOrder);
        }

        var inventory = new CashInventory
        {
            Number = await GenerateInventoryNumberAsync(date, ct),
            Date = date,
            AccountName = accountName,
            MoneyAccountId = matchingAccounts[0].Id,
            CompanyId = matchingAccounts[0].CompanyId,
            AccountingBalance = accountingBalance,
            ActualBalance = request.ActualBalance,
            Difference = difference,
            CorrectionCashOrder = correctionOrder,
            Comment = Normalize(request.Comment),
            CreatedAt = DateTime.UtcNow,
        };

        _db.CashInventories.Add(inventory);
        _db.Entry(inventory).Property<int?>("CreatedByUserId").CurrentValue = visibility.UserId;
        await _db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);

        return Created($"/api/cash-orders/inventories/{inventory.Id}", Map(inventory));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id, CancellationToken ct)
    {
        if (await RequirePaymentsPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolvePaymentsVisibilityAsync(ct);
        if (visibility is null)
        {
            return Forbid();
        }

        var order = await CompanyCashOrders
            .Include(item => item.Client)
            .Include(item => item.Payment)
            .Include(item => item.MoneyOperation)
            .AsNoTracking()
            .ApplyCashOrderVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);

        return order is null ? NotFound() : Ok(Map(order));
    }

    [HttpPost("from-payment/{paymentId:int}")]
    public async Task<IActionResult> CreateFromPayment(int paymentId, CancellationToken ct)
    {
        if (await RequirePaymentsPermissionAsync(static permission => permission.CanCreate, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolvePaymentsVisibilityAsync(ct);
        if (visibility is null)
        {
            return Forbid();
        }

        var payment = await CompanyPayments
            .Include(item => item.Client)
            .Include(item => item.PaymentSource)
            .ApplyPaymentVisibility(visibility)
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

        var existing = await CompanyCashOrders
            .Where(order => order.PaymentId == payment.Id && order.Status != CashOrderStatus.Cancelled)
            .OrderByDescending(order => order.Id)
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            return Ok(Map(existing));
        }

        var orderType = payment.Type == PaymentType.Expense ? CashOrderType.Outgoing : CashOrderType.Incoming;
        var orderDate = (payment.PaymentDocumentDate ?? payment.LastPaymentDate ?? payment.PaidDate ?? payment.Date).Date;
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
            CreatedByUserId = User.GetUserId(),
            CreatedByUserName = User.GetDisplayName(),
        };

        _db.CashOrders.Add(order);
        await _db.SaveChangesAsync(ct);

        return Created($"/api/cash-orders/{order.Id}", Map(order));
    }

    [HttpPost("from-money-operation/{operationId:int}")]
    public async Task<IActionResult> CreateFromMoneyOperation(int operationId, CancellationToken ct)
    {
        if (await RequirePaymentsPermissionAsync(static permission => permission.CanCreate, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolvePaymentsVisibilityAsync(ct);
        if (visibility is null)
        {
            return Forbid();
        }

        var operation = await ApplyVisibleMoneyOperationForCashOrders(_db.MoneyOperations, visibility)
            .FirstOrDefaultAsync(item => item.Id == operationId, ct);
        if (operation is null)
        {
            return NotFound();
        }

        if (!ShouldCreateCashOrder(operation))
        {
            return BadRequest("Кассовый ордер можно создать только по фактической операции кассы с суммой больше нуля.");
        }

        var existing = await CompanyCashOrders
            .Where(order => order.MoneyOperationId == operation.Id && order.Status != CashOrderStatus.Cancelled)
            .OrderByDescending(order => order.Id)
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            return Ok(Map(existing));
        }

        if (string.IsNullOrWhiteSpace(operation.Currency))
        {
            return BadRequest("В денежной операции не указана валюта. Уточните её перед созданием кассового ордера.");
        }

        MoneyAccount? confirmedAccount = null;
        if (operation.MoneyAccountId.HasValue)
        {
            if (_companyAccess is null) return Forbid();
            confirmedAccount = await BankStatementImportAccess.CoherentAccounts(_db,
                    BankStatementImportAccess.ScopeAccounts(_db.MoneyAccounts.AsNoTracking(), _companyAccess, ignoreQueryFilters: false),
                    forNewOperations: true)
                .SingleOrDefaultAsync(account => account.Id == operation.MoneyAccountId && account.Source == MoneyOperationSource.Cash, ct);
            if (confirmedAccount is null || !string.Equals(confirmedAccount.Currency.Trim(), operation.Currency.Trim(), StringComparison.OrdinalIgnoreCase))
                return BadRequest("Касса операции недоступна или её валюта не совпадает с валютой операции.");
        }

        await using var transaction = _db.Database.CurrentTransaction is null
            ? await _db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct)
            : null;
        if (confirmedAccount is not null)
        {
            // Match the company -> account -> operation lock order of money mutations.
            if (await _db.LegalEntities.Where(company => company.Id == confirmedAccount.CompanyId
                    && company.ProfileStatus == CompanyProfileStatuses.Active)
                .ExecuteUpdateAsync(setters => setters.SetProperty(company => company.ShortName, company => company.ShortName), ct) != 1
                || await _db.MoneyAccounts.Where(account => account.Id == confirmedAccount.Id && account.IsActive
                    && account.CompanyId == confirmedAccount.CompanyId && account.Source == MoneyOperationSource.Cash
                    && account.Currency == confirmedAccount.Currency)
                .ExecuteUpdateAsync(setters => setters.SetProperty(account => account.Name, account => account.Name), ct) != 1)
                return Conflict("Касса операции изменилась. Обновите данные и повторите действие.");
        }
        if (await _db.MoneyOperations.Where(item => item.Id == operation.Id && item.MoneyAccountId == operation.MoneyAccountId
                && item.UpdatedAt == operation.UpdatedAt && item.Source == operation.Source && item.Direction == operation.Direction
                && item.AccountingType == operation.AccountingType && item.Amount == operation.Amount
                && item.Currency == operation.Currency && item.Date == operation.Date
                && item.AccountName == operation.AccountName && item.CounterpartyName == operation.CounterpartyName
                && item.Purpose == operation.Purpose && item.CategoryName == operation.CategoryName)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Purpose, item => item.Purpose), ct) != 1)
            return Conflict("Денежная операция изменилась. Обновите данные и повторите действие.");

        if (await RequirePaymentsPermissionAsync(static permission => permission.CanCreate, ct) is { } changedPermission)
            return changedPermission;
        var freshVisibility = await ResolvePaymentsVisibilityAsync(ct);
        if (freshVisibility is null || !await ApplyVisibleMoneyOperationForCashOrders(_db.MoneyOperations.AsNoTracking(), freshVisibility)
                .AnyAsync(item => item.Id == operation.Id, ct))
            return NotFound();
        if (confirmedAccount is not null && (_companyAccess is null || !await BankStatementImportAccess.CoherentAccounts(_db,
                BankStatementImportAccess.ScopeAccounts(_db.MoneyAccounts.AsNoTracking(), _companyAccess, ignoreQueryFilters: false),
                forNewOperations: true).AnyAsync(account => account.Id == confirmedAccount.Id && account.CompanyId == confirmedAccount.CompanyId, ct)))
            return Forbid();
        existing = await CompanyCashOrders.AsNoTracking()
            .Where(order => order.MoneyOperationId == operation.Id && order.Status != CashOrderStatus.Cancelled)
            .OrderByDescending(order => order.Id).FirstOrDefaultAsync(ct);
        if (existing is not null) return Ok(Map(existing));

        var orderType = operation.Direction == MoneyOperationDirection.Expense ? CashOrderType.Outgoing : CashOrderType.Incoming;
        var order = new CashOrder
        {
            Number = await GenerateNumberAsync(orderType, operation.Date, ct),
            Type = orderType,
            Status = CashOrderStatus.Posted,
            Date = operation.Date.Date,
            Amount = operation.Amount,
            Currency = operation.Currency.Trim(),
            MoneyOperationId = operation.Id,
            MoneyAccountId = confirmedAccount?.Id,
            CompanyId = confirmedAccount?.CompanyId,
            Account = operation.AccountName,
            PayerName = operation.CounterpartyName,
            ReceiverName = operation.Direction == MoneyOperationDirection.Expense ? operation.CounterpartyName : null,
            Basis = BuildBasis(operation),
            Comment = operation.CategoryName,
            CreatedAt = DateTime.UtcNow,
            CreatedByUserId = User.GetUserId(),
            CreatedByUserName = User.GetDisplayName(),
        };

        _db.CashOrders.Add(order);
        await _db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);

        return Created($"/api/cash-orders/{order.Id}", Map(order));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CashOrder model, CancellationToken ct)
    {
        if (await RequirePaymentsPermissionAsync(static permission => permission.CanCreate, ct) is { } forbidden)
        {
            return forbidden;
        }

        if (model.Client is not null || model.Payment is not null || model.MoneyOperation is not null || model.Invoice is not null)
            return BadRequest("Связанные платежи, денежные операции, счета и клиентов создавайте отдельно. Для ордера по существующему документу используйте действие в исходной записи.");

        if (model.Amount <= 0m)
        {
            return BadRequest("Сумма кассового ордера должна быть больше нуля.");
        }

        if (model.Status == CashOrderStatus.Posted && string.IsNullOrWhiteSpace(model.Currency))
        {
            return BadRequest("Укажите валюту перед проведением кассового ордера.");
        }

        await using var transaction = model.SelectedMoneyAccountId.HasValue && _db.Database.CurrentTransaction is null
            ? await _db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct)
            : null;
        if (model.SelectedMoneyAccountId.HasValue)
        {
            if (model.PaymentId.HasValue || model.MoneyOperationId.HasValue || model.InvoiceId.HasValue || model.ClientId.HasValue
                || model.Payment is not null || model.MoneyOperation is not null || model.Client is not null)
                return BadRequest("Для связанного документа используйте создание ордера из исходной операции.");
            if (await ResolvePaymentsVisibilityAsync(ct) is null || _companyAccess is null) return Forbid();
            var selected = await BankStatementImportAccess.CoherentAccounts(_db,
                    BankStatementImportAccess.ScopeAccounts(_db.MoneyAccounts.AsNoTracking(), _companyAccess, ignoreQueryFilters: false),
                    forNewOperations: true)
                .SingleOrDefaultAsync(account => account.Id == model.SelectedMoneyAccountId && account.Source == MoneyOperationSource.Cash, ct);
            if (selected is null || string.IsNullOrWhiteSpace(selected.Currency)
                || !string.Equals(selected.Currency.Trim(), model.Currency?.Trim(), StringComparison.OrdinalIgnoreCase))
                return BadRequest("Касса недоступна или её валюта не совпадает с валютой ордера.");
            if (await _db.LegalEntities.Where(company => company.Id == selected.CompanyId && company.ProfileStatus == CompanyProfileStatuses.Active)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(company => company.ShortName, company => company.ShortName), ct) != 1
                || await _db.MoneyAccounts.Where(account => account.Id == selected.Id && account.IsActive && account.CompanyId == selected.CompanyId
                    && account.Source == MoneyOperationSource.Cash && account.Currency == selected.Currency && account.Name == selected.Name)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(account => account.Name, account => account.Name), ct) != 1)
                return Conflict("Касса изменилась. Обновите данные и повторите действие.");
            if (await RequirePaymentsPermissionAsync(static permission => permission.CanCreate, ct) is { } changedPermission) return changedPermission;
            if (await ResolvePaymentsVisibilityAsync(ct) is null || _companyAccess is null
                || !await BankStatementImportAccess.CoherentAccounts(_db,
                    BankStatementImportAccess.ScopeAccounts(_db.MoneyAccounts.AsNoTracking(), _companyAccess, ignoreQueryFilters: false),
                    forNewOperations: true).AnyAsync(account => account.Id == selected.Id && account.CompanyId == selected.CompanyId, ct))
                return Forbid();
            model.MoneyAccountId = selected.Id;
            model.CompanyId = selected.CompanyId;
            model.Account = selected.Name;
        }

        var today = await ResolveCashOrderTodayAsync(ct);
        model.Date = model.Date == default ? today : model.Date.Date;
        model.Number = string.IsNullOrWhiteSpace(model.Number)
            ? await GenerateNumberAsync(model.Type, model.Date, ct)
            : model.Number.Trim();
        model.Currency = string.IsNullOrWhiteSpace(model.Currency) ? null : model.Currency.Trim();
        model.Account = Normalize(model.Account);
        model.PayerName = Normalize(model.PayerName);
        model.ReceiverName = Normalize(model.ReceiverName);
        model.Basis = Normalize(model.Basis);
        model.Appendix = Normalize(model.Appendix);
        model.Comment = Normalize(model.Comment);
        model.CancelledAt = model.Status == CashOrderStatus.Cancelled ? model.CancelledAt ?? DateTime.UtcNow : null;
        model.CancelledReason = model.Status == CashOrderStatus.Cancelled ? Normalize(model.CancelledReason) : null;
        model.CreatedAt = DateTime.UtcNow;
        model.CreatedByUserId = User.GetUserId();
        model.CreatedByUserName = User.GetDisplayName();

        _db.CashOrders.Add(model);
        await _db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);

        return Created($"/api/cash-orders/{model.Id}", Map(model));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] CashOrder model, CancellationToken ct)
    {
        if (await RequirePaymentsPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolvePaymentsVisibilityAsync(ct);
        if (visibility is null)
        {
            return Forbid();
        }

        var order = await CompanyCashOrders
            .ApplyCashOrderVisibility(visibility)
            .AsTracking()
            .FirstOrDefaultAsync(item => item.Id == id, ct);
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

        if (order.MoneyAccountId.HasValue && (Normalize(model.Account) != Normalize(order.Account)
            || model.MoneyOperationId != order.MoneyOperationId || model.PaymentId != order.PaymentId || model.InvoiceId != order.InvoiceId))
        {
            return BadRequest("Нельзя изменить кассу или связанные документы у ордера с подтверждённой кассой.");
        }
        if (order.MoneyAccountId.HasValue && !string.Equals(Normalize(model.Currency), Normalize(order.Currency), StringComparison.OrdinalIgnoreCase))
            return BadRequest("Нельзя изменить валюту ордера с подтверждённой кассой.");

        order.Type = model.Type;
        order.Date = model.Date == default ? order.Date : model.Date.Date;
        order.Amount = model.Amount;
        order.Currency = string.IsNullOrWhiteSpace(model.Currency) ? null : model.Currency.Trim();
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
        order.UpdatedByUserId = User.GetUserId();
        order.UpdatedByUserName = User.GetDisplayName();

        await _db.SaveChangesAsync(ct);

        return Ok(Map(order));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        if (await RequirePaymentsPermissionAsync(static permission => permission.CanDelete, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolvePaymentsVisibilityAsync(ct);
        if (visibility is null)
        {
            return Forbid();
        }

        var order = await CompanyCashOrders
            .ApplyCashOrderVisibility(visibility)
            .AsTracking()
            .FirstOrDefaultAsync(item => item.Id == id, ct);
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

    [HttpPost("{id:int}/post")]
    public async Task<IActionResult> Post(int id, CancellationToken ct)
    {
        if (await RequirePaymentsPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolvePaymentsVisibilityAsync(ct);
        if (visibility is null)
        {
            return Forbid();
        }

        var order = await CompanyCashOrders
            .ApplyCashOrderVisibility(visibility)
            .AsTracking()
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (order is null)
        {
            return NotFound();
        }

        if (order.Status == CashOrderStatus.Cancelled)
        {
            return BadRequest("Отменённый кассовый ордер нельзя провести.");
        }

        if (order.Amount <= 0m)
        {
            return BadRequest("Сумма кассового ордера должна быть больше нуля.");
        }

        if (order.Status != CashOrderStatus.Posted && string.IsNullOrWhiteSpace(order.Currency))
        {
            return BadRequest("Укажите валюту перед проведением кассового ордера.");
        }

        if (order.Status != CashOrderStatus.Posted)
        {
            await using var transaction = order.MoneyAccountId.HasValue && _db.Database.CurrentTransaction is null
                ? await _db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct)
                : null;
            if (order.MoneyAccountId.HasValue)
            {
                if (_companyAccess is null || !order.CompanyId.HasValue) return BadRequest("Принадлежность кассы ордера не подтверждена.");
                var account = await BankStatementImportAccess.CoherentAccounts(_db,
                        BankStatementImportAccess.ScopeAccounts(_db.MoneyAccounts.AsNoTracking(), _companyAccess, ignoreQueryFilters: false),
                        forNewOperations: true)
                    .SingleOrDefaultAsync(item => item.Id == order.MoneyAccountId && item.CompanyId == order.CompanyId
                        && item.Source == MoneyOperationSource.Cash, ct);
                if (account is null || string.IsNullOrWhiteSpace(account.Currency)
                    || !string.Equals(account.Currency.Trim(), order.Currency?.Trim(), StringComparison.OrdinalIgnoreCase))
                    return BadRequest("Касса недоступна или её валюта не совпадает с валютой ордера.");
                if (await _db.LegalEntities.Where(company => company.Id == account.CompanyId && company.ProfileStatus == CompanyProfileStatuses.Active)
                        .ExecuteUpdateAsync(setters => setters.SetProperty(company => company.ShortName, company => company.ShortName), ct) != 1
                    || await _db.MoneyAccounts.Where(item => item.Id == account.Id && item.IsActive && item.CompanyId == account.CompanyId
                        && item.Source == MoneyOperationSource.Cash && item.Currency == account.Currency)
                        .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Name, item => item.Name), ct) != 1)
                    return Conflict("Касса изменилась. Обновите данные и повторите действие.");
                if (await _db.CashOrders.Where(item => item.Id == order.Id && item.MoneyAccountId == order.MoneyAccountId
                        && item.CompanyId == order.CompanyId && item.UpdatedAt == order.UpdatedAt && item.Status == order.Status
                        && item.Amount == order.Amount && item.Currency == order.Currency)
                        .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Number, item => item.Number), ct) != 1)
                    return Conflict("Ордер изменился. Обновите данные и повторите действие.");
                if (await RequirePaymentsPermissionAsync(static permission => permission.CanEdit, ct) is { } changedPermission) return changedPermission;
                var freshVisibility = await ResolvePaymentsVisibilityAsync(ct);
                if (freshVisibility is null || _companyAccess is null
                    || !await CompanyCashOrders.AsNoTracking().ApplyCashOrderVisibility(freshVisibility).AnyAsync(item => item.Id == order.Id, ct)
                    || !await BankStatementImportAccess.CoherentAccounts(_db,
                        BankStatementImportAccess.ScopeAccounts(_db.MoneyAccounts.AsNoTracking(), _companyAccess, ignoreQueryFilters: false),
                        forNewOperations: true).AnyAsync(item => item.Id == account.Id && item.CompanyId == account.CompanyId, ct))
                    return Forbid();
            }
            order.Status = CashOrderStatus.Posted;
            order.UpdatedAt = DateTime.UtcNow;
            order.UpdatedByUserId = User.GetUserId();
            order.UpdatedByUserName = User.GetDisplayName();
            await _db.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
        }

        return Ok(Map(order));
    }

    [HttpPost("{id:int}/cancel")]
    public async Task<IActionResult> Cancel(int id, [FromBody] CashOrderCancelRequest? request, CancellationToken ct)
    {
        if (await RequirePaymentsPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolvePaymentsVisibilityAsync(ct);
        if (visibility is null)
        {
            return Forbid();
        }

        var order = await CompanyCashOrders
            .ApplyCashOrderVisibility(visibility)
            .AsTracking()
            .FirstOrDefaultAsync(item => item.Id == id, ct);
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
            order.UpdatedByUserId = User.GetUserId();
            order.UpdatedByUserName = User.GetDisplayName();
            await _db.SaveChangesAsync(ct);
        }

        return Ok(Map(order));
    }

    [HttpPost("{id:int}/copy")]
    public async Task<IActionResult> Copy(int id, CancellationToken ct)
    {
        if (await RequirePaymentsPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolvePaymentsVisibilityAsync(ct);
        if (visibility is null)
        {
            return Forbid();
        }

        var source = await CompanyCashOrders
            .AsNoTracking()
            .ApplyCashOrderVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (source is null)
        {
            return NotFound();
        }

        await using var transaction = source.MoneyAccountId.HasValue && _db.Database.CurrentTransaction is null
            ? await _db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct)
            : null;
        MoneyAccount? selectedAccount = null;
        if (source.MoneyAccountId.HasValue)
        {
            if (_companyAccess is null || !source.CompanyId.HasValue) return BadRequest("Принадлежность кассы ордера не подтверждена.");
            selectedAccount = await BankStatementImportAccess.CoherentAccounts(_db,
                    BankStatementImportAccess.ScopeAccounts(_db.MoneyAccounts.AsNoTracking(), _companyAccess, ignoreQueryFilters: false),
                    forNewOperations: true)
                .SingleOrDefaultAsync(account => account.Id == source.MoneyAccountId && account.CompanyId == source.CompanyId
                    && account.Source == MoneyOperationSource.Cash, ct);
            if (selectedAccount is null || string.IsNullOrWhiteSpace(source.Currency) || string.IsNullOrWhiteSpace(selectedAccount.Currency)
                || !string.Equals(source.Currency.Trim(), selectedAccount.Currency.Trim(), StringComparison.OrdinalIgnoreCase))
                return BadRequest("Касса недоступна или её валюта не совпадает с валютой ордера.");
            if (await _db.LegalEntities.Where(company => company.Id == selectedAccount.CompanyId && company.ProfileStatus == CompanyProfileStatuses.Active)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(company => company.ShortName, company => company.ShortName), ct) != 1
                || await _db.MoneyAccounts.Where(account => account.Id == selectedAccount.Id && account.IsActive
                    && account.CompanyId == selectedAccount.CompanyId && account.Source == MoneyOperationSource.Cash
                    && account.Currency == selectedAccount.Currency && account.Name == selectedAccount.Name)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(account => account.Name, account => account.Name), ct) != 1)
                return Conflict("Касса изменилась. Обновите данные и повторите действие.");
            if (await _db.CashOrders.Where(order => order.Id == source.Id && order.MoneyAccountId == source.MoneyAccountId
                    && order.CompanyId == source.CompanyId && order.UpdatedAt == source.UpdatedAt && order.Status == source.Status
                    && order.Amount == source.Amount && order.Currency == source.Currency && order.Account == source.Account)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(order => order.Number, order => order.Number), ct) != 1)
                return Conflict("Ордер изменился. Обновите данные и повторите действие.");
            if (await RequirePaymentsPermissionAsync(static permission => permission.CanEdit, ct) is { } changedPermission) return changedPermission;
            var freshVisibility = await ResolvePaymentsVisibilityAsync(ct);
            if (freshVisibility is null || _companyAccess is null
                || !await CompanyCashOrders.AsNoTracking().ApplyCashOrderVisibility(freshVisibility).AnyAsync(order => order.Id == source.Id, ct)
                || !await BankStatementImportAccess.CoherentAccounts(_db,
                    BankStatementImportAccess.ScopeAccounts(_db.MoneyAccounts.AsNoTracking(), _companyAccess, ignoreQueryFilters: false),
                    forNewOperations: true).AnyAsync(account => account.Id == selectedAccount.Id && account.CompanyId == selectedAccount.CompanyId, ct))
                return Forbid();
        }

        var today = await ResolveCashOrderTodayAsync(ct);
        var order = new CashOrder
        {
            Number = await GenerateNumberAsync(source.Type, today, ct),
            Type = source.Type,
            Status = CashOrderStatus.Draft,
            Date = today,
            Amount = source.Amount,
            Currency = string.IsNullOrWhiteSpace(source.Currency) ? null : source.Currency.Trim(),
            ClientId = source.ClientId,
            InvoiceId = source.InvoiceId,
            Account = selectedAccount?.Name ?? source.Account,
            MoneyAccountId = selectedAccount?.Id,
            CompanyId = selectedAccount?.CompanyId,
            PayerName = source.PayerName,
            ReceiverName = source.ReceiverName,
            Basis = source.Basis,
            Appendix = source.Appendix,
            Comment = source.Comment,
            CreatedAt = DateTime.UtcNow,
            CreatedByUserId = User.GetUserId(),
            CreatedByUserName = User.GetDisplayName(),
        };

        _db.CashOrders.Add(order);
        await _db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);

        return Created($"/api/cash-orders/{order.Id}", Map(order));
    }

    [HttpGet("{id:int}/print")]
    public async Task<IActionResult> Print(int id, CancellationToken ct)
    {
        if (await RequirePaymentsPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolvePaymentsVisibilityAsync(ct);
        if (visibility is null)
        {
            return Forbid();
        }

        var order = await CompanyCashOrders
            .Include(item => item.Client)
            .Include(item => item.Payment)
            .Include(item => item.MoneyOperation)
            .AsNoTracking()
            .ApplyCashOrderVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (order is null)
        {
            return NotFound();
        }

        var printContext = await ResolvePrintContextAsync(order, ct);
        return Content(BuildPrintHtml(order, printContext), "text/html; charset=utf-8", Encoding.UTF8);
    }

    [HttpGet("payment/{paymentId:int}/print")]
    public async Task<IActionResult> PrintForPayment(int paymentId, CancellationToken ct)
    {
        if (await RequirePaymentsPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolvePaymentsVisibilityAsync(ct);
        if (visibility is null)
        {
            return Forbid();
        }

        var payment = await CompanyPayments
            .Include(item => item.Client)
            .Include(item => item.ExpenseClient)
            .Include(item => item.LegalEntity)
            .Include(item => item.ExpenseLegalEntity)
            .AsNoTracking()
            .ApplyPaymentVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == paymentId, ct);
        if (payment is null)
        {
            return NotFound();
        }

        var order = await CompanyCashOrders
            .Include(item => item.Client)
            .Include(item => item.Payment)
            .Include(item => item.MoneyOperation)
            .AsNoTracking()
            .ApplyCashOrderVisibility(visibility)
            .Where(item => item.PaymentId == paymentId && item.Status != CashOrderStatus.Cancelled)
            .OrderByDescending(item => item.Id)
            .FirstOrDefaultAsync(ct)
            ?? BuildCashOrderPrintPreview(payment);

        var printContext = await ResolvePrintContextAsync(order, ct);
        return Content(BuildPrintHtml(order, printContext), "text/html; charset=utf-8", Encoding.UTF8);
    }

    [HttpGet("{id:int}/pdf")]
    public async Task<IActionResult> DownloadPdf(int id, CancellationToken ct)
    {
        if (await RequirePaymentsPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolvePaymentsVisibilityAsync(ct);
        if (visibility is null)
        {
            return Forbid();
        }

        var order = await CompanyCashOrders
            .Include(item => item.Client)
            .Include(item => item.Payment)
            .Include(item => item.MoneyOperation)
            .AsNoTracking()
            .ApplyCashOrderVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (order is null)
        {
            return NotFound();
        }

        try
        {
            var printContext = await ResolvePrintContextAsync(order, ct);
            var pdfBytes = await RenderPdfAsync(BuildPrintHtml(order, printContext), ct);
            return File(pdfBytes, "application/pdf", $"cash-order-{SanitizeFilePart(order.Number)}.pdf");
        }
        catch (InvalidOperationException ex)
        {
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, detail: ex.Message);
        }
    }

    [HttpGet("{id:int}/word")]
    public async Task<IActionResult> DownloadWord(int id, CancellationToken ct)
    {
        if (await RequirePaymentsPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolvePaymentsVisibilityAsync(ct);
        if (visibility is null)
        {
            return Forbid();
        }

        var order = await CompanyCashOrders
            .Include(item => item.Client)
            .Include(item => item.Payment)
            .Include(item => item.MoneyOperation)
            .AsNoTracking()
            .ApplyCashOrderVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (order is null)
        {
            return NotFound();
        }

        var printContext = await ResolvePrintContextAsync(order, ct);
        var html = BuildPrintHtml(order, printContext);
        var bytes = order.Type == CashOrderType.Incoming
            ? PayPlanner.Api.Services.Documents.CashOrderWordDocumentRenderer.RenderIncoming(html)
            : PayPlanner.Api.Services.Documents.CashOrderWordDocumentRenderer.RenderOutgoing(html);
        return File(
            bytes,
            PayPlanner.Api.Services.Documents.HtmlWordDocumentBuilder.ContentType,
            $"cash-order-{SanitizeFilePart(order.Number)}.docx");
    }

    private static bool IsCashPayment(Payment payment)
        => payment.PaymentSource?.Name?.Contains(CashSourceToken, StringComparison.OrdinalIgnoreCase) == true ||
           payment.PaymentSource?.Name?.Contains(CashRegisterSourceToken, StringComparison.OrdinalIgnoreCase) == true;

    private static CashOrder BuildCashOrderPrintPreview(Payment payment)
    {
        var type = payment.Type == PaymentType.Expense
            ? CashOrderType.Outgoing
            : CashOrderType.Incoming;
        var date = (payment.PaymentDocumentDate
            ?? payment.LastPaymentDate
            ?? payment.PaidDate
            ?? payment.Date).Date;
        var amount = payment.PaidAmount > 0m ? payment.PaidAmount : payment.Amount;
        var counterpartyName = type == CashOrderType.Outgoing
            ? payment.ExpenseClient?.Name
                ?? payment.ExpenseLegalEntity?.ShortName
                ?? payment.ExpenseLegalEntity?.FullName
                ?? payment.CounterpartyName
                ?? payment.Client?.Name
                ?? payment.LegalEntity?.ShortName
                ?? payment.LegalEntity?.FullName
            : payment.Client?.Name
                ?? payment.LegalEntity?.ShortName
                ?? payment.LegalEntity?.FullName
                ?? payment.CounterpartyName;
        var basis = BuildBasis(payment);

        return new CashOrder
        {
            Number = Normalize(payment.PaymentOrderNumber)
                ?? payment.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Type = type,
            Status = payment.Status == PaymentStatus.Cancelled
                ? CashOrderStatus.Cancelled
                : payment.Status == PaymentStatus.Completed && payment.PaidAmount > 0m
                    ? CashOrderStatus.Posted
                    : CashOrderStatus.Draft,
            Date = date,
            Amount = amount,
            Currency = "RUB",
            ClientId = type == CashOrderType.Outgoing ? payment.ExpenseClientId ?? payment.ClientId : payment.ClientId,
            Client = type == CashOrderType.Outgoing ? payment.ExpenseClient ?? payment.Client : payment.Client,
            PaymentId = payment.Id,
            Payment = payment,
            InvoiceId = payment.InvoiceId,
            Account = payment.Account,
            PayerName = type == CashOrderType.Incoming ? counterpartyName : null,
            ReceiverName = type == CashOrderType.Outgoing ? counterpartyName : null,
            Basis = basis,
            Comment = Normalize(payment.Notes),
            CancelledAt = payment.Status == PaymentStatus.Cancelled ? payment.AuditUpdatedAt ?? DateTime.UtcNow : null,
            CancelledReason = payment.Status == PaymentStatus.Cancelled ? "Платеж отменен." : null,
            CreatedAt = payment.CreatedAt,
            CreatedByUserId = payment.AuditCreatedByUserId,
            CreatedByUserName = payment.AuditCreatedByUserName,
        };
    }

    private static bool ShouldCreateCashOrder(MoneyOperation operation)
        => operation.Source == MoneyOperationSource.Cash &&
           operation.AccountingType == MoneyOperationAccountingType.Fact &&
           operation.Amount > 0m &&
           (operation.Direction == MoneyOperationDirection.Income ||
            operation.Direction == MoneyOperationDirection.Expense);

    private IQueryable<MoneyOperation> ApplyVisibleMoneyOperationForCashOrders(
        IQueryable<MoneyOperation> query,
        DataVisibilityAccess visibility)
    {
        query = BankStatementImportAccess.ScopeMoneyOperations(_db, query, _companyAccess);
        if (visibility.SeesAll)
        {
            return query;
        }

        var visibleUserIds = visibility.VisibleUserIds.ToArray();
        return query.Where(operation =>
            (operation.ResponsibleUserId.HasValue && visibleUserIds.Contains(operation.ResponsibleUserId.Value))
            || (operation.LinkedInvoice != null
                && operation.LinkedInvoice.Payments.Any(payment =>
                    payment.DocumentKind == DocumentKind.Invoice
                    && (payment.Responsibles.Any(link => visibleUserIds.Contains(link.UserId))
                        || (payment.Client != null
                            && payment.Client.ResponsibleUserId.HasValue
                            && visibleUserIds.Contains(payment.Client.ResponsibleUserId.Value))
                        || (payment.ExpenseClient != null
                            && payment.ExpenseClient.ResponsibleUserId.HasValue
                            && visibleUserIds.Contains(payment.ExpenseClient.ResponsibleUserId.Value))
                        || (payment.LegalEntity != null
                            && payment.LegalEntity.ResponsibleUserId.HasValue
                            && visibleUserIds.Contains(payment.LegalEntity.ResponsibleUserId.Value))
                        || (payment.ExpenseLegalEntity != null
                            && payment.ExpenseLegalEntity.ResponsibleUserId.HasValue
                            && visibleUserIds.Contains(payment.ExpenseLegalEntity.ResponsibleUserId.Value))
                        || (payment.ClientCase != null
                            && payment.ClientCase.Client != null
                            && payment.ClientCase.Client.ResponsibleUserId.HasValue
                            && visibleUserIds.Contains(payment.ClientCase.Client.ResponsibleUserId.Value))
                        || (payment.ClientCase != null
                            && payment.ClientCase.LegalEntity != null
                            && payment.ClientCase.LegalEntity.ResponsibleUserId.HasValue
                            && visibleUserIds.Contains(payment.ClientCase.LegalEntity.ResponsibleUserId.Value))
                        || payment.InvoiceClientCases.Any(link =>
                            (link.ClientCase.Client != null
                                && link.ClientCase.Client.ResponsibleUserId.HasValue
                                && visibleUserIds.Contains(link.ClientCase.Client.ResponsibleUserId.Value))
                            || (link.ClientCase.LegalEntity != null
                                && link.ClientCase.LegalEntity.ResponsibleUserId.HasValue
                                && visibleUserIds.Contains(link.ClientCase.LegalEntity.ResponsibleUserId.Value)))
                        || (payment.Contract != null
                            && (payment.Contract.ClientContracts.Any(link =>
                                    link.Client.ResponsibleUserId.HasValue
                                    && visibleUserIds.Contains(link.Client.ResponsibleUserId.Value))
                                || payment.Contract.LegalEntityContracts.Any(link =>
                                    link.LegalEntity.ResponsibleUserId.HasValue
                                    && visibleUserIds.Contains(link.LegalEntity.ResponsibleUserId.Value))))))));
    }

    private async Task<DateTime> ResolveCashOrderTodayAsync(CancellationToken ct)
    {
        var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct);
        return CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date;
    }

    private async Task<CashBookResponseDto?> BuildCashBookAsync(
        DateTime? from,
        DateTime? to,
        string? accountName,
        string? counterpartyName,
        int? clientId,
        int? caseId,
        int? contractId,
        int? incomeTypeId,
        DataVisibilityAccess visibility,
        CancellationToken ct,
        int? moneyAccountId = null)
    {
        var fromDate = from?.Date ?? await ResolveCashOrderTodayAsync(ct);
        var toDate = (to ?? fromDate).Date;
        if (toDate < fromDate)
        {
            (fromDate, toDate) = (toDate, fromDate);
        }

        var postedOrders = CompanyCashOrders
            .AsNoTracking()
            .ApplyCashOrderVisibility(visibility)
            .Where(order => order.Status == CashOrderStatus.Posted);
        var accountFilter = NormalizeAccountFilter(accountName);
        if (moneyAccountId.HasValue)
        {
            postedOrders = postedOrders.Where(order => order.MoneyAccountId == moneyAccountId.Value);
        }
        else if (accountFilter is not null)
        {
            postedOrders = postedOrders.Where(order => order.Account == accountFilter);
        }
        postedOrders = ApplyCashBookFilters(postedOrders, counterpartyName, clientId, caseId, contractId, incomeTypeId);

        var aggregateGroups = await postedOrders
            .Where(order => order.Date <= toDate)
            .GroupBy(order => new
            {
                IsOpening = order.Date < fromDate,
                Date = order.Date < fromDate ? fromDate : order.Date.Date,
            })
            .Select(group => new
            {
                group.Key.IsOpening,
                group.Key.Date,
                BalanceChange = group.Sum(order => order.Type == CashOrderType.Incoming ? order.Amount : -order.Amount),
                Income = group.Where(order => order.Type == CashOrderType.Incoming).Sum(order => order.Amount),
                Expense = group.Where(order => order.Type == CashOrderType.Outgoing).Sum(order => order.Amount),
                IncomingCount = group.Count(order => order.Type == CashOrderType.Incoming),
                OutgoingCount = group.Count(order => order.Type == CashOrderType.Outgoing),
                HasUnknownCurrency = group.Any(order => order.Currency == null || order.Currency.Trim() == ""),
                MinCurrency = group.Min(order => order.Currency == null ? null : order.Currency.Trim().ToUpper()),
                MaxCurrency = group.Max(order => order.Currency == null ? null : order.Currency.Trim().ToUpper()),
            })
            .OrderBy(group => group.Date)
            .ToListAsync(ct);
        var (currencyStatus, currency) = ResolveCashBookCurrency(aggregateGroups.Select(group =>
            (group.HasUnknownCurrency, group.MinCurrency, group.MaxCurrency)));
        if (currencyStatus is "mixed" or "unknown") return null;
        var openingBalance = aggregateGroups
            .Where(group => group.IsOpening)
            .Sum(group => group.BalanceChange);
        var periodGroups = aggregateGroups
            .Where(group => !group.IsOpening)
            .ToArray();
        var periodGroupsByDate = periodGroups.ToDictionary(group => group.Date);

        var runningBalance = openingBalance;
        var days = new List<CashBookDayDto>((toDate - fromDate).Days + 1);
        for (var date = fromDate; date <= toDate; date = date.AddDays(1))
        {
            periodGroupsByDate.TryGetValue(date, out var group);
            var dayOpening = runningBalance;
            var dayIncome = group?.Income ?? 0m;
            var dayExpense = group?.Expense ?? 0m;
            runningBalance += dayIncome - dayExpense;
            days.Add(new CashBookDayDto
            {
                Date = date,
                OpeningBalance = dayOpening,
                Income = dayIncome,
                Expense = dayExpense,
                ClosingBalance = runningBalance,
                IncomingCount = group?.IncomingCount ?? 0,
                OutgoingCount = group?.OutgoingCount ?? 0,
            });
        }

        var income = periodGroups.Sum(group => group.Income);
        var expense = periodGroups.Sum(group => group.Expense);

        return new CashBookResponseDto
        {
            From = fromDate,
            To = toDate,
            AccountName = accountFilter,
            MoneyAccountId = moneyAccountId,
            CurrencyStatus = currencyStatus,
            Currency = currency,
            OpeningBalance = openingBalance,
            Income = income,
            Expense = expense,
            ClosingBalance = openingBalance + income - expense,
            IncomingCount = periodGroups.Sum(group => group.IncomingCount),
            OutgoingCount = periodGroups.Sum(group => group.OutgoingCount),
            Days = days,
        };
    }

    private IQueryable<CashOrder> ApplyCashBookFilters(
        IQueryable<CashOrder> query,
        string? counterpartyName,
        int? clientId,
        int? caseId,
        int? contractId,
        int? incomeTypeId)
    {
        if (!string.IsNullOrWhiteSpace(counterpartyName))
        {
            var counterpartyFilter = counterpartyName.Trim();
            query = query.Where(item =>
                (item.PayerName != null && item.PayerName.Contains(counterpartyFilter)) ||
                (item.ReceiverName != null && item.ReceiverName.Contains(counterpartyFilter)) ||
                (item.PaymentId.HasValue && _db.Payments.Any(payment =>
                    payment.Id == item.PaymentId.Value &&
                    payment.CounterpartyName != null &&
                    payment.CounterpartyName.Contains(counterpartyFilter))) ||
                (item.MoneyOperationId.HasValue && _db.MoneyOperations.Any(operation =>
                    operation.Id == item.MoneyOperationId.Value &&
                    operation.CounterpartyName != null &&
                    operation.CounterpartyName.Contains(counterpartyFilter))));
        }

        if (clientId.HasValue && clientId.Value > 0)
        {
            var targetClientId = clientId.Value;
            query = query.Where(item =>
                item.ClientId == targetClientId ||
                (item.PaymentId.HasValue && _db.Payments.Any(payment =>
                    payment.Id == item.PaymentId.Value &&
                    (payment.ClientId == targetClientId || payment.ExpenseClientId == targetClientId))) ||
                (item.MoneyOperationId.HasValue && _db.MoneyOperations.Any(operation =>
                    operation.Id == item.MoneyOperationId.Value &&
                    operation.CounterpartyName != null &&
                    _db.Clients.Any(client =>
                        client.Id == targetClientId &&
                        ((!string.IsNullOrEmpty(client.Name) && operation.CounterpartyName.Contains(client.Name)) ||
                            (!string.IsNullOrEmpty(client.Company) && operation.CounterpartyName.Contains(client.Company)))))));
        }

        if (caseId.HasValue && caseId.Value > 0)
        {
            var targetCaseId = caseId.Value;
            query = query.Where(item =>
                (item.PaymentId.HasValue && _db.Payments.Any(payment =>
                    payment.Id == item.PaymentId.Value &&
                    (payment.ClientCaseId == targetCaseId ||
                        payment.InvoiceClientCases.Any(link => link.ClientCaseId == targetCaseId)))) ||
                (item.MoneyOperationId.HasValue && _db.MoneyOperations.Any(operation =>
                    operation.Id == item.MoneyOperationId.Value &&
                    operation.LinkedInvoiceId.HasValue &&
                    _db.Payments.Any(payment =>
                        payment.InvoiceId == operation.LinkedInvoiceId.Value &&
                        (payment.ClientCaseId == targetCaseId ||
                            payment.InvoiceClientCases.Any(link => link.ClientCaseId == targetCaseId))))));
        }

        if (contractId.HasValue && contractId.Value > 0)
        {
            var targetContractId = contractId.Value;
            query = query.Where(item =>
                (item.PaymentId.HasValue && _db.Payments.Any(payment =>
                    payment.Id == item.PaymentId.Value &&
                    (payment.ContractId == targetContractId ||
                        _db.ContractPayments.Any(link => link.ContractId == targetContractId && link.PaymentId == payment.Id) ||
                        _db.ContractInvoices.Any(link => link.ContractId == targetContractId && link.InvoiceId == payment.Id)))) ||
                (item.MoneyOperationId.HasValue && _db.MoneyOperations.Any(operation =>
                    operation.Id == item.MoneyOperationId.Value &&
                    operation.LinkedInvoiceId.HasValue &&
                    _db.Payments.Any(payment =>
                        payment.InvoiceId == operation.LinkedInvoiceId.Value &&
                        (payment.ContractId == targetContractId ||
                            _db.ContractPayments.Any(link => link.ContractId == targetContractId && link.PaymentId == payment.Id) ||
                            _db.ContractInvoices.Any(link => link.ContractId == targetContractId && link.InvoiceId == payment.Id))))));
        }

        if (incomeTypeId.HasValue && incomeTypeId.Value > 0)
        {
            var targetIncomeTypeId = incomeTypeId.Value;
            query = query.Where(item =>
                (item.PaymentId.HasValue && _db.Payments.Any(payment =>
                    payment.Id == item.PaymentId.Value &&
                    payment.IncomeTypeId == targetIncomeTypeId)) ||
                (item.MoneyOperationId.HasValue && _db.MoneyOperations.Any(operation =>
                    operation.Id == item.MoneyOperationId.Value &&
                    operation.IncomeTypeId == targetIncomeTypeId)));
        }

        return query;
    }

    private IQueryable<CashOrder> BuildFilteredQuery(
        DateTime? from,
        DateTime? to,
        CashOrderType? type,
        CashOrderStatus? status,
        string? search,
        string? accountName,
        DataVisibilityAccess visibility,
        int? moneyAccountId = null)
    {
        var query = CompanyCashOrders
            .AsNoTracking()
            .ApplyCashOrderVisibility(visibility)
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

        var accountFilter = NormalizeAccountFilter(accountName);
        if (moneyAccountId.HasValue)
        {
            query = query.Where(order => order.MoneyAccountId == moneyAccountId.Value);
        }
        else if (accountFilter is not null)
        {
            query = query.Where(order => order.Account == accountFilter);
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

    private static string? NormalizeAccountFilter(string? accountName)
    {
        var value = accountName?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private async Task<string> GenerateNumberAsync(CashOrderType type, DateTime date, CancellationToken ct)
    {
        var prefix = type == CashOrderType.Incoming ? "ПКО" : "РКО";
        var yearPrefix = $"{prefix}-{date:yyyy}-";
        // Soft-deleted documents retain their globally unique numbers.
        var reservedOrders = _db.CashOrders.IgnoreQueryFilters().AsNoTracking();
        var sequence = await reservedOrders.CountAsync(order => order.Number.StartsWith(yearPrefix), ct) + 1;

        while (true)
        {
            var number = $"{yearPrefix}{sequence:0000}";
            if (!await reservedOrders.AnyAsync(order => order.Number == number, ct))
            {
                return number;
            }

            sequence++;
        }
    }

    private async Task<string> GenerateInventoryNumberAsync(DateTime date, CancellationToken ct)
    {
        var yearPrefix = $"ИНВК-{date:yyyy}-";
        var count = await _db.CashInventories.CountAsync(item => item.Number.StartsWith(yearPrefix), ct);
        return $"{yearPrefix}{count + 1:0000}";
    }

    private static string BuildBasis(Payment payment)
    {
        var account = string.IsNullOrWhiteSpace(payment.Account) ? null : $"счет №{payment.Account.Trim()}";
        var description = string.IsNullOrWhiteSpace(payment.Description) ? null : payment.Description.Trim();
        var action = payment.Type == PaymentType.Expense ? "Выдача наличных" : "Оплата от клиента";
        return string.Join(". ", new[] { action, account, description }.Where(item => !string.IsNullOrWhiteSpace(item)));
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

    private static readonly System.Linq.Expressions.Expression<Func<CashOrder, CashOrderDto>> CashOrderListProjection = order => new CashOrderDto
    {
        Id = order.Id,
        Number = order.Number,
        Type = order.Type,
        Status = order.Status,
        Date = order.Date,
        Amount = order.Amount,
        Currency = order.Currency,
        ClientId = order.ClientId,
        ClientName = order.Client != null ? order.Client.Name : null,
        PaymentId = order.PaymentId,
        MoneyOperationId = order.MoneyOperationId,
        InvoiceId = order.InvoiceId,
        OriginType = order.PaymentId.HasValue
            ? "payment"
            : order.MoneyOperationId.HasValue
                ? "moneyOperation"
                : (order.Basis ?? string.Empty).ToLower().Contains("inventory") ||
                    (order.Comment ?? string.Empty).ToLower().Contains("inventory")
                    ? "inventory"
                    : "manual",
        Account = order.Account,
        HasLinkedCashAccount = order.MoneyAccountId.HasValue,
        PayerName = order.PayerName,
        ReceiverName = order.ReceiverName,
        Basis = order.Basis,
        Appendix = order.Appendix,
        Comment = order.Comment,
        CancelledAt = order.CancelledAt,
        CancelledReason = order.CancelledReason,
        CreatedAt = order.CreatedAt,
        CreatedByUserId = order.CreatedByUserId,
        CreatedByUserName = order.CreatedByUserName,
        UpdatedAt = order.UpdatedAt,
        UpdatedByUserId = order.UpdatedByUserId,
        UpdatedByUserName = order.UpdatedByUserName,
    };

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
            OriginType = ResolveCashOrderOriginType(order),
            Account = order.Account,
            HasLinkedCashAccount = order.MoneyAccountId.HasValue,
            PayerName = order.PayerName,
            ReceiverName = order.ReceiverName,
            Basis = order.Basis,
            Appendix = order.Appendix,
            Comment = order.Comment,
            CancelledAt = order.CancelledAt,
            CancelledReason = order.CancelledReason,
            CreatedAt = order.CreatedAt,
            CreatedByUserId = order.CreatedByUserId,
            CreatedByUserName = order.CreatedByUserName,
            UpdatedAt = order.UpdatedAt,
            UpdatedByUserId = order.UpdatedByUserId,
            UpdatedByUserName = order.UpdatedByUserName,
        };

    private static string ResolveCashOrderOriginType(CashOrder order)
    {
        if (order.PaymentId.HasValue)
        {
            return "payment";
        }

        if (order.MoneyOperationId.HasValue)
        {
            return "moneyOperation";
        }

        if ((order.Basis ?? string.Empty).Contains("inventory", StringComparison.OrdinalIgnoreCase) ||
            (order.Comment ?? string.Empty).Contains("inventory", StringComparison.OrdinalIgnoreCase))
        {
            return "inventory";
        }

        return "manual";
    }

    private static CashInventoryDto Map(CashInventory inventory)
        => new()
        {
            Id = inventory.Id,
            Number = inventory.Number,
            Date = inventory.Date,
            AccountName = inventory.AccountName,
            AccountingBalance = inventory.AccountingBalance,
            ActualBalance = inventory.ActualBalance,
            Difference = inventory.Difference,
            CorrectionCashOrderId = inventory.CorrectionCashOrderId,
            CorrectionCashOrderNumber = inventory.CorrectionCashOrder?.Number,
            Comment = inventory.Comment,
            CreatedAt = inventory.CreatedAt,
            UpdatedAt = inventory.UpdatedAt,
        };

    private async Task<CashOrderPrintContext> ResolvePrintContextAsync(CashOrder order, CancellationToken ct)
    {
        var configured = _configuration?["App:OrganizationName"]?.Trim();
        var rawId = await _db.SystemSettings.AsNoTracking()
            .Where(item => item.Key == "legalEntities.defaultMainCompanyId"
                || item.Key == "legalEntities.mainCompanyId")
            .OrderByDescending(item => item.Key == "legalEntities.defaultMainCompanyId")
            .Select(item => item.Value)
            .FirstOrDefaultAsync(ct);
        if (int.TryParse(rawId, out var id) && id > 0)
        {
            var legalEntity = await _db.LegalEntities.AsNoTracking()
                .Where(item => item.Id == id)
                .Select(item => new
                {
                    Name = item.ShortName ?? item.FullName,
                    item.Director,
                    item.DocumentDirectorJson,
                    item.ChiefAccountantJson,
                    item.VatWithoutVat,
                })
                .FirstOrDefaultAsync(ct);
            if (legalEntity is not null)
            {
                var director = ParseCashOrderPerson(legalEntity.DocumentDirectorJson);
                var chiefAccountant = ParseCashOrderPerson(legalEntity.ChiefAccountantJson);
                var fallbackSigner = ShortCashOrderSigner(order.CreatedByUserName);
                return new CashOrderPrintContext(
                    string.IsNullOrWhiteSpace(configured)
                        ? (legalEntity.Name ?? order.CreatedByUserName ?? "Организация не указана")
                        : configured,
                    director?.Position ?? string.Empty,
                    ShortCashOrderPerson(director, legalEntity.Director ?? order.CreatedByUserName),
                    ShortCashOrderPerson(chiefAccountant, order.CreatedByUserName),
                    fallbackSigner,
                    BuildCashOrderTaxText(order, legalEntity.VatWithoutVat));
            }
        }

        var signer = ShortCashOrderSigner(order.CreatedByUserName);
        return new CashOrderPrintContext(
            string.IsNullOrWhiteSpace(configured) ? (order.CreatedByUserName ?? "Организация не указана") : configured,
            string.Empty,
            signer,
            signer,
            signer,
            "Без налога (НДС) 0,00 руб.");
    }

    private static string BuildIncomingCashOrderPrintHtml(CashOrder order, CashOrderPrintContext context)
    {
        var number = Html(order.Number);
        var organization = Html(context.OrganizationName);
        var payerText = order.PayerName ?? order.Client?.Name ?? string.Empty;
        var basisText = order.Basis ?? string.Empty;
        var appendixText = order.Appendix ?? string.Empty;
        var payer = Html(payerText);
        var basis = Html(basisText);
        var appendix = Html(appendixText);
        var chiefAccountant = Html(context.ChiefAccountantName);
        var cashier = Html(context.CashierName);
        var taxText = Html(context.TaxText);
        var fontCss = BuildCashOrderFontCss();
        var amountRubKop = Html(FormatCashOrderRubKop(order.Amount));
        var rubles = Html(FormatWholeRubles(order.Amount));
        var kopecks = Html(FormatKopecks(order.Amount));
        var amountWordsText = AmountRublesToWordsRu(order.Amount);
        var amountWords = Html(amountWordsText);
        var month = GetRussianMonthGenitive(order.Date.Month);
        var numberClass = order.Number.Length > 32
            ? " number-very-compact"
            : order.Number.Length > 16
                ? " number-compact"
                : string.Empty;
        var amountClass = amountRubKop.Length > 10 ? " amount-compact" : string.Empty;
        var denseLayout = payerText.Length > 55
            || basisText.Length > 80
            || appendixText.Length > 70
            || amountWordsText.Length > 55;
        var layoutClass = denseLayout ? " dense-content" : string.Empty;
        var cancelNotice = order.Status == CashOrderStatus.Cancelled
            ? $"""<div class="cancelled">Документ отменён{(order.CancelledAt.HasValue ? $" {order.CancelledAt:dd.MM.yyyy HH:mm}" : string.Empty)}{(string.IsNullOrWhiteSpace(order.CancelledReason) ? string.Empty : $": {Html(order.CancelledReason)}")}</div>"""
            : string.Empty;

        return $$"""
<!doctype html>
<html lang="ru">
<head>
  <meta charset="utf-8" />
  <title>Приходный кассовый ордер {{number}}</title>
  <style>
    {{fontCss}}
    @page { size: A4 portrait; margin: 0; }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      color: #333;
      font-family: "Cash Order Arial", Arial, sans-serif;
      font-size: 10px;
      line-height: 1.15;
      background: #fff;
      -webkit-print-color-adjust: exact !important;
      print-color-adjust: exact !important;
    }
    .ko1 {
      display: grid;
      grid-template-columns: 101.75mm 11mm 63.25mm;
      gap: 1mm;
      width: 178mm;
      margin: 27mm 0 0 16mm;
    }
    .order, .receipt { min-width: 0; position: relative; }
    .receipt { left: 1.75mm; width: calc(100% + 1.85mm); }
    .cut { position: relative; min-height: 132.6mm; border-left: 1px solid #111; border-right: 2px solid #111; }
    .cut::before { content: ""; position: absolute; top: 0; bottom: 0; left: 7mm; border-left: 1px dashed #111; }
    .cut span { position: absolute; top: 42mm; left: 50%; transform: translateX(-50%); writing-mode: vertical-rl; letter-spacing: 5px; font-weight: 700; font-size: 8px; }
    .top-note { position: relative; top: 3px; margin-left: 57.85mm; margin-bottom: 6.15mm; font-size: 6pt; line-height: 1.05; transform: scaleX(.957); transform-origin: left top; }
    .org-line { border-bottom: 2px solid #111; min-height: 18px; padding: 0 6px 2px; }
    .hint { color: #333; font-size: 6px; text-align: center; }
    .header-row { display: block; padding-right: 30.75mm; padding-bottom: 4.8mm; margin-top: 11.7mm; }
    .header-row > div { position: relative; top: .5mm; }
    .header-row .org-line { min-height: 16px; font-size: 8.75px; }
    .header-row .org-line > span { position: relative; top: -5px; }
    .order .header-row .org-line > span { left: 31px; }
    .header-row > div > .org-line:first-child { min-height: 8.5px; height: 8.5px; }
    .header-row > div > .org-line:nth-child(3) { min-height: 19px; height: 19px; }
    .header-row .hint { line-height: .7; }
    .order .org-line { padding-left: 5px; }
    .code-box, .doc-meta, .ledger { border-collapse: collapse; width: 100%; table-layout: fixed; }
    .code-box td, .doc-meta td, .ledger th, .ledger td { border: 1px solid #111; padding: 1.5mm 1mm; text-align: center; vertical-align: middle; }
    .code-box { position: absolute; top: 8.58mm; right: -.25mm; width: 32.5mm; font-size: 6pt; }
    .code-box td { padding: 0 1mm; }
    .code-box td:first-child { width: 17mm; border: 0; text-align: right; white-space: nowrap; }
    .code-box td:last-child { width: 15.5mm; font-weight: 700; }
    .code-box tr:nth-child(1), .code-box tr:nth-child(2), .code-box tr:nth-child(3) { height: 4mm; }
    .code-box tr:nth-child(1) td:first-child { font-size: 7pt; }
    .code-box tr:nth-child(4) { height: 6.55mm; }
    .title { width: 62mm; margin: 3.5mm 0 5mm 4.55mm; position: relative; top: 1.42mm; text-align: center; font-size: 9pt; line-height: 1.05; font-weight: 700; transform: scaleX(.99); transform-origin: center top; text-transform: uppercase; }
    .doc-meta { position: absolute; top: 30.2mm; right: 0; width: 30.7mm; margin: 0; font-size: 5.5pt; }
    .doc-meta td { padding: 0 .2mm; line-height: 1; }
    .doc-meta tr:nth-child(1) { height: 6.8mm; }
    .doc-meta tr:nth-child(2) { height: 4.8mm; }
    .doc-meta td:nth-child(2) { padding-left: .2mm; }
    .doc-meta .value { font-size: 5pt; font-weight: 700; white-space: nowrap; }
    .doc-meta .number-compact { font-size: 4.5pt !important; line-height: 1; white-space: nowrap; letter-spacing: -.1pt; }
    .doc-meta .number-very-compact { font-size: 2.25pt !important; line-height: 1; white-space: nowrap; letter-spacing: -.2pt; }
    .ledger { font-size: 6pt; }
    .ledger th { font-weight: 400; }
    .ledger th, .ledger td { padding: 0 1pt; }
    .ledger { position: relative; top: 8.825pt; margin-bottom: -8.4pt; }
    .ledger tr:nth-child(1) { height: 11.5pt; }
    .ledger tr:nth-child(2) { height: 33.75pt; }
    .ledger tr:nth-child(3) { height: 12pt; }
    .ledger th[rowspan] { vertical-align: top; padding-top: 2pt; }
    .ledger tr:nth-child(2) th:nth-child(1) { padding-top: 0; }
    .ledger tr:nth-child(2) th:nth-child(2) { padding-top: 1.6pt; }
    .ledger tr:nth-child(2) th:nth-child(3) { padding-top: 3.5pt; }
    .ledger .sum { font-size: 6pt; white-space: nowrap; padding-top: .2pt; }
    .ledger .sum.amount-compact { font-size: 4pt; letter-spacing: -.15pt; }
    .line-field { display: grid; grid-template-columns: auto 1fr; gap: 2mm; align-items: end; margin-top: 1.4mm; font-size: 6pt; }
    .line { min-height: 4mm; border-bottom: 1px solid #111; padding: 0 1.5mm .4mm; }
    .long-line { margin-top: 1.4mm; min-height: 4.2mm; border-bottom: 1px solid #111; }
    .amount-words { display: grid; grid-template-columns: auto 1fr auto 18mm auto 12mm auto; gap: 2mm; align-items: end; margin-top: 1.8mm; font-size: 6pt; }
    .amount-words .line { text-align: left; }
    .amount-words .kopecks, .receipt .kopecks { text-align: center; }
    .sign-row { display: grid; grid-template-columns: 28mm 1fr 1fr; gap: 6mm; align-items: end; margin-top: 2mm; font-size: 6pt; }
    .sign-line { border-bottom: 1px solid #111; min-height: 4.4mm; text-align: center; padding-bottom: .4mm; }
    .sign-label { font-size: 6px; text-align: center; color: #333; margin-top: .5mm; }
    .receipt { height: 370pt; }
    .receipt .org-line { position: absolute; top: 8.75pt; left: 0; width: 100%; margin: 0; text-align: center; font-size: 6.3pt; }
    .receipt .org-line > span { position: relative; top: 2.25pt; left: 3px; }
    .receipt > .hint { position: absolute; top: 23pt; left: 0; width: 100%; font-size: 5pt; }
    .receipt-title { position: absolute; top: 38pt; left: 2.5px; width: 100%; text-align: center; font-size: 10.3pt; font-weight: 700; margin: 0; text-transform: uppercase; }
    .receipt-subtitle { margin: 0; font-size: 6pt; }
    .receipt-number { display: grid; grid-template-columns: 1fr 20mm; gap: 1mm; align-items: end; }
    .receipt-number .line { padding-left: 1mm; padding-right: 1mm; font-size: 6pt; white-space: nowrap; }
    .receipt-number > span { white-space: nowrap; font-size: 6.5pt; }
    .receipt-number .number-compact { font-size: 4pt !important; white-space: nowrap; }
    .receipt-number .number-very-compact { font-size: 2.5pt !important; white-space: nowrap; }
    .receipt-date { display: grid; grid-template-columns: auto 7mm auto 18mm 13mm auto; gap: 2mm; align-items: end; margin: 0; font-size: 6pt; }
    .receipt .line-field { grid-template-columns: auto 1fr; margin: 0; }
    .receipt .amount-digits { display: grid; grid-template-columns: auto 1fr auto 10mm auto; gap: 2mm; align-items: end; margin: 0; font-size: 6pt; }
    .receipt .amount-digits .line { text-align: center; }
    .receipt .amount-words { display: block; margin-top: 0; }
    .receipt .amount-words .line { width: 100%; }
    .receipt-words-digits { position: absolute; top: 228pt; left: 0; width: 100%; height: 12pt; display: block; font-size: 6pt; }
    .receipt-words-digits > * { position: absolute; bottom: 0; }
    .receipt-words-digits > div:nth-child(1) { left: 0; width: 35.5mm; }
    .receipt-words-digits > span:nth-child(2) { left: 37.8mm; }
    .receipt-words-digits > div:nth-child(3) { left: 45.2mm; width: 10mm; }
    .receipt-words-digits > span:nth-child(4) { left: 56.4mm; }
    .receipt .small-hint { text-align: center; font-size: 5pt; color: #333; margin: 0; }
    .receipt .stamp-row { display: grid; grid-template-columns: 1fr auto 18mm auto 13mm auto; gap: 2mm; align-items: end; margin: 0; font-size: 6pt; }
    .receipt .sign-row { grid-template-columns: 24mm 1fr 1fr; gap: 4mm; margin: 0; }
    .receipt-number { position: absolute; top: 75.2pt; left: 0; width: 100%; }
    .receipt-date-main { position: absolute; top: 86.4pt; left: 0; width: 100%; }
    .payer-field { position: absolute; top: 103.35pt; left: 0; width: 100%; }
    .basis-field { position: absolute; top: 129.7pt; left: 0; width: 100%; }
    .receipt-lines { position: absolute; top: 148.9pt; left: 0; width: 100%; height: 25.3pt; border-top: 1px solid #111; border-bottom: 1px solid #111; }
    .receipt-lines::after { content: ""; position: absolute; left: 0; right: 0; top: 11.55pt; border-top: 1px solid #111; }
    .receipt-digits { position: absolute !important; top: 183pt; left: 0; width: 100%; height: 12pt; display: block !important; }
    .receipt-digits > * { position: absolute; bottom: 0; }
    .receipt-digits > span:nth-child(1) { left: 0; }
    .receipt-digits > div:nth-child(2) { left: 11.5mm; width: 24.5mm; }
    .receipt-digits > span:nth-child(3) { left: 38mm; }
    .receipt-digits > div:nth-child(4) { left: 45.45mm; width: 9.25mm; }
    .receipt-digits > span:nth-child(5) { left: 56.4mm; }
    .digits-hint { position: absolute; top: 195pt; left: 0; width: 100%; }
    .receipt-words { position: absolute !important; top: 202.45pt; left: 0; width: 100%; }
    .words-hint { position: absolute; top: 217.5pt; left: 0; width: 100%; }
    .tax-field { position: absolute; top: 244.95pt; left: 0; width: 100%; }
    .receipt-stamp-date { position: absolute; top: 260.5pt; left: 0; width: 100%; }
    .stamp-label { position: absolute; top: 287.5pt; left: 0; margin: 0 !important; font-size: 6pt; }
    .chief-sign { position: absolute; top: 305.05pt; left: 0; width: 100%; }
    .cashier-sign { position: absolute; top: 325.15pt; left: 0; width: 100%; }
    .order .line-field > span { position: relative; top: -4.3pt; }
    .order .line-field { font-size: 6.4pt; }
    .order .line-field .line { padding-bottom: 0; }
    .order .line-field > span { top: -3pt; }
    .order .amount-words, .order .sign-row { font-size: 6.4pt; }
    .order .amount-words .line, .order .sign-line { padding-bottom: 0; }
    .order-tax > span, .order-tax .positioned-text { position: relative; top: 3pt; }
    .order-appendix > span, .order-appendix .positioned-text { position: relative; top: 1.85pt; }
    .order-received > span, .order-received .positioned-text { position: relative; top: .75pt; }
    .order-amount .positioned-text { position: relative; top: 3pt; }
    .order-chief > div:first-child, .order-chief .positioned-text { position: relative; top: 2.25pt; }
    .order-cashier > div:first-child, .order-cashier .positioned-text { position: relative; top: 3pt; }
    .order-received { position: absolute; top: 196.85pt; left: 0; width: 100%; margin: 0; }
    .order-basis { position: absolute; top: 212pt; left: 0; width: 100%; margin: 0; }
    .order-blank { position: absolute; top: 225.3pt; left: 0; width: 100%; height: 10pt; margin: 0; }
    .order-amount { position: absolute; top: 234.25pt; left: 0; width: 100%; height: 29pt; margin: 0; display: block !important; }
    .order-amount > * { position: absolute; margin: 0; }
    .order-amount > span:nth-child(1) { left: 0; top: 0; }
    .order-amount > div:nth-child(2) { left: 28.7pt; top: 3.75pt; width: 259.55pt; }
    .order-amount > span:nth-child(3) { left: 193.7pt; top: 12.95pt; }
    .order-amount > div:nth-child(4) { left: 217.1pt; top: 12.95pt; width: 49pt; }
    .order-amount > span:nth-child(5) { left: 264.3pt; top: 12.95pt; }
    .order-amount-hint { position: absolute; top: 254.5pt; left: 139.5pt; margin: 0; font-size: 5pt; }
    .order-tax { position: absolute; top: 272.95pt; left: 0; width: 100%; margin: 0; }
    .order-appendix { position: absolute; top: 287.75pt; left: 0; width: 100%; margin: 0; }
    .order-chief { position: absolute; top: 303.7pt; left: 0; width: 100%; margin: 0; grid-template-columns: 76pt 88pt 96pt 23.5pt; gap: 0; align-items: start; }
    .order-cashier { position: absolute; top: 323.15pt; left: 0; width: 100%; margin: 0; grid-template-columns: 76pt 88pt 96pt 23.5pt; gap: 0; align-items: start; }
    .order-chief > div:nth-child(3), .order-cashier > div:nth-child(3) { grid-column: 3; }
    .receipt-number, .receipt-date-main, .payer-field, .basis-field, .receipt-digits, .digits-hint, .receipt-words, .words-hint, .tax-field, .receipt-stamp-date { transform: translateY(-4.2pt); }
    .order-amount .line { position: relative; left: -4.6pt; }
    .order-amount::after { content: ""; position: absolute; top: 34pt; left: 0; width: 194.5pt; border-top: 1px solid #111; }
    .receipt-words .line { padding-left: 3pt; }
    .receipt-words { transform: translateY(-3.55pt); }
    .receipt-number { transform: translateY(-3.8pt); }
    .receipt-number > span { position: relative; top: -3pt; }
    .receipt-date-main { transform: translateY(-2.7pt); }
    .payer-field { transform: translateY(-2pt); }
    .payer-field > span { position: relative; top: -3.75pt; }
    .receipt-payer-blank { position: absolute; top: 122.45pt; left: 0; width: 100%; border-top: 1px solid #111; }
    .basis-field { transform: translateY(-2.65pt); }
    .basis-field > span { position: relative; top: -3pt; }
    .basis-field > span { font-size: 6.5pt; }
    .receipt-digits { transform: translateY(-1.4pt); }
    .digits-hint { top: 195pt; }
    .receipt-words .positioned-text { position: relative; top: 1.5pt; }
    .receipt-words { font-size: 6.4pt; }
    .words-hint { top: 213.25pt; }
    .stamp-label { top: 289pt; }
    .receipt .sign-row { align-items: start; }
    .receipt .sign-line { padding-bottom: 0; }
    .tax-field > span, .tax-field .positioned-text, .receipt-stamp-date > * { position: relative; top: 1.5pt; }
    .tax-field .line { font-size: 6.75pt; }
    .chief-sign > div:first-child, .chief-sign .positioned-text { position: relative; top: 1.5pt; }
    .cashier-sign > div:first-child, .cashier-sign .positioned-text { position: relative; top: 2.25pt; }
    .chief-sign, .cashier-sign { display: block; }
    .chief-sign > div:nth-child(2), .chief-sign > div:nth-child(3), .cashier-sign > div:nth-child(2), .cashier-sign > div:nth-child(3) { position: absolute; top: 0; }
    .chief-sign > div:nth-child(2) { left: 24.25mm; width: 12.2mm; }
    .chief-sign > div:nth-child(3) { left: 38.65mm; width: 26.95mm; }
    .cashier-sign > div:nth-child(2) { left: 11.25mm; width: 12mm; }
    .cashier-sign > div:nth-child(3) { left: 26.65mm; width: 30.1mm; }
    .receipt .cashier-sign .sign-label { position: relative; top: .75pt; }
    .order > .cancelled { position: absolute; top: 340pt; left: 0; width: 100%; margin: 0; border: 1px solid #111; padding: 3px 5px; font-size: 5.5pt; line-height: 1.15; font-weight: 700; }
    .ko1.dense-content .order-received { align-items: start; }
    .ko1.dense-content .order-received .line { min-height: 18pt; max-height: 18pt; padding-top: 0; font-size: 4.5pt; line-height: 1.12; overflow-wrap: anywhere; }
    .ko1.dense-content .order-basis { top: 212pt; align-items: start; }
    .ko1.dense-content .order-basis .line { min-height: 22pt; max-height: 22pt; padding-top: 0; font-size: 4.5pt; line-height: 1.12; overflow-wrap: anywhere; }
    .ko1.dense-content .order-blank { display: none; }
    .ko1.dense-content .order-amount { top: 242pt; height: 31pt; }
    .ko1.dense-content .order-amount > div:nth-child(2) { width: 240pt; max-height: 14pt; padding: 0; font-size: 4.5pt; line-height: 1.08; overflow-wrap: anywhere; }
    .ko1.dense-content .order-amount-hint { top: 257pt; }
    .ko1.dense-content .order-tax { top: 279pt; }
    .ko1.dense-content .order-appendix { top: 293pt; align-items: start; }
    .ko1.dense-content .order-appendix .line { min-height: 17pt; max-height: 17pt; padding-top: 0; font-size: 4.5pt; line-height: 1.12; overflow-wrap: anywhere; }
    .ko1.dense-content .order-chief { top: 314pt; }
    .ko1.dense-content .order-cashier { top: 333pt; }
    .ko1.dense-content .payer-field { align-items: start; }
    .ko1.dense-content .payer-field .line { min-height: 20pt; max-height: 20pt; padding-top: 0; font-size: 4.5pt; line-height: 1.12; overflow-wrap: anywhere; }
    .ko1.dense-content .basis-field { top: 126pt; align-items: start; z-index: 1; }
    .ko1.dense-content .basis-field .line { min-height: 34pt; max-height: 34pt; padding-top: 0; font-size: 4.5pt; line-height: 1.12; overflow-wrap: anywhere; }
    .ko1.dense-content .receipt-words .line { max-height: 14pt; font-size: 4.5pt; line-height: 1.08; overflow-wrap: anywhere; }
    .ko1.dense-content .doc-meta { width: 45mm; }
    .ko1.dense-content .doc-meta td:first-child { width: 29mm; }
    .ko1.dense-content .doc-meta td:last-child { width: 16mm; }
    .ko1.dense-content .doc-meta .number-very-compact { font-size: 2.25pt !important; line-height: 1; }
    .ko1.dense-content .receipt-lines { top: 164pt; }
    .ko1.dense-content .receipt-digits { top: 194pt; }
    .ko1.dense-content .digits-hint { top: 206pt; }
    .ko1.dense-content .receipt-words { top: 209pt; }
    .ko1.dense-content .words-hint { top: 224pt; }
    .ko1.dense-content .tax-field { top: 250pt; }
    .ko1.dense-content .receipt-stamp-date { top: 273pt; }
    .ko1.dense-content .stamp-label { top: 286pt; }
    .ko1.dense-content .chief-sign { top: 308pt; }
    .ko1.dense-content .cashier-sign { top: 333pt; }
  </style>
</head>
<body>
  <main class="ko1{{layoutClass}}">
    <div hidden><span class="word-chief">{{chiefAccountant}}</span><span class="word-cashier">{{cashier}}</span><span class="word-tax">{{taxText}}</span></div>
    <section class="order">
      <div class="top-note">
        <div>Унифицированная форма № КО-1</div>
        <div>Утверждена постановлением Госкомстата</div>
        <div>России от 18.08.98 № 88</div>
      </div>
      <div class="header-row">
        <div>
          <div class="org-line"><span>{{organization}}</span></div>
          <div class="hint">(организация)</div>
          <div class="org-line"></div>
          <div class="hint">(структурное подразделение)</div>
        </div>
        <table class="code-box">
          <tr><td>Код</td><td></td></tr>
          <tr><td>Форма по ОКУД</td><td>0310002</td></tr>
          <tr><td>по ОКПО</td><td></td></tr>
          <tr><td></td><td></td></tr>
        </table>
      </div>
      <table class="doc-meta">
        <tr><td>Номер<br>документа</td><td>Дата<br>составления</td></tr>
        <tr><td class="value{{numberClass}}">{{number}}</td><td class="value">{{order.Date:dd.MM.yyyy}}</td></tr>
      </table>
      <h1 class="title">Приходный кассовый ордер</h1>
      {{cancelNotice}}
      <table class="ledger">
        <colgroup><col style="width:10mm"><col style="width:18mm"><col style="width:18mm"><col style="width:18mm"><col style="width:14mm"><col style="width:22mm"></colgroup>
        <tr>
          <th rowspan="2" style="width:58px;">Дебет</th>
          <th colspan="3">Кредит</th>
          <th rowspan="2" style="width:78px;">Сумма<br>руб. коп.</th>
          <th rowspan="2" style="width:104px;">Код целевого<br>назначения</th>
        </tr>
        <tr>
          <th>код струк-<br>турного<br>подразде-<br>ления</th>
          <th>корреспон-<br>дирующий счет,<br>субсчет</th>
          <th>код аналити-<br>ческого учета</th>
        </tr>
        <tr>
          <td>&nbsp;</td><td>&nbsp;</td><td>&nbsp;</td><td>&nbsp;</td><td class="sum{{amountClass}}">{{amountRubKop}}</td><td>&nbsp;</td>
        </tr>
      </table>
      <div class="line-field order-received"><span>Принято от</span><div class="line"><span class="positioned-text">{{payer}}</span></div></div>
      <div class="line-field order-basis"><span>Основание:</span><div class="line">{{basis}}</div></div>
      <div class="long-line order-blank"></div>
      <div class="amount-words order-amount">
        <span><span class="positioned-text">Сумма</span></span><div class="line"><span class="positioned-text">{{amountWords}}</span></div><span><span class="positioned-text">руб.</span></span><div class="line kopecks"><span class="positioned-text">{{kopecks}}</span></div><span><span class="positioned-text">коп.</span></span>
      </div>
      <div class="order-amount-hint">(прописью)</div>
      <div class="line-field order-tax" style="grid-template-columns:auto 1fr;"><span>В том числе</span><div class="line"><span class="positioned-text">{{taxText}}</span></div></div>
      <div class="line-field order-appendix"><span>Приложение</span><div class="line"><span class="positioned-text">{{appendix}}</span></div></div>
      <div class="sign-row order-chief">
        <div>Главный бухгалтер</div>
        <div><div class="sign-line"></div><div class="sign-label">(подпись)</div></div>
        <div><div class="sign-line"><span class="positioned-text">{{chiefAccountant}}</span></div><div class="sign-label">(расшифровка подписи)</div></div>
      </div>
      <div class="sign-row order-cashier">
        <div>Получил кассир</div>
        <div><div class="sign-line"></div><div class="sign-label">(подпись)</div></div>
        <div><div class="sign-line"><span class="positioned-text">{{cashier}}</span></div><div class="sign-label">(расшифровка подписи)</div></div>
      </div>
    </section>
    <div class="cut"><span>Линия отреза</span></div>
    <section class="receipt">
      <div class="org-line"><span>{{organization}}</span></div>
      <div class="hint">(организация)</div>
      <div class="receipt-title">Квитанция</div>
      <div class="receipt-subtitle receipt-number"><span>к приходному кассовому ордеру №</span><div class="line{{numberClass}}">{{number}}</div></div>
      <div class="receipt-date receipt-date-main"><span>от</span><span>"</span><div class="line">{{order.Date.Day}}</div><span>" {{month}}</span><div class="line">{{order.Date:yyyy}}</div><span>г.</span></div>
      <div class="line-field payer-field"><span>Принято от</span><div class="line"></div></div>
      <div class="receipt-payer-blank"></div>
      <div class="line-field basis-field"><span>Основание:</span><div class="line">{{basis}}</div></div>
      <div class="receipt-lines"></div>
      <div class="amount-digits receipt-digits"><span>Сумма</span><div class="line">{{rubles}}</div><span>руб.</span><div class="line">{{kopecks}}</div><span>коп.</span></div>
      <div class="small-hint digits-hint">(цифрами)</div>
      <div class="amount-words receipt-words"><div class="line"><span class="positioned-text">{{amountWords}}</span></div></div>
      <div class="small-hint words-hint">(прописью)</div>
      <div class="receipt-words-digits"><div class="line"></div><span>руб.</span><div class="line kopecks">{{kopecks}}</div><span>коп.</span></div>
      <div class="line-field tax-field" style="grid-template-columns:auto 1fr;"><span>В том числе</span><div class="line"><span class="positioned-text">{{taxText}}</span></div></div>
      <div class="stamp-row receipt-stamp-date"><span>"</span><div class="line">{{order.Date.Day}}</div><span>" {{month}}</span><div class="line">{{order.Date:yyyy}}</div><span>г.</span></div>
      <div class="stamp-label">М.П. (штампа)</div>
      <div class="sign-row chief-sign">
        <div>Главный бухгалтер</div>
        <div><div class="sign-line"></div><div class="sign-label">(подпись)</div></div>
        <div><div class="sign-line"><span class="positioned-text">{{chiefAccountant}}</span></div><div class="sign-label">(расшифровка подписи)</div></div>
      </div>
      <div class="sign-row cashier-sign">
        <div>Кассир</div>
        <div><div class="sign-line"></div><div class="sign-label">(подпись)</div></div>
        <div><div class="sign-line"><span class="positioned-text">{{cashier}}</span></div><div class="sign-label">(расшифровка подписи)</div></div>
      </div>
    </section>
  </main>
</body>
</html>
""";
    }

    private static string BuildPrintHtml(CashOrder order, CashOrderPrintContext context)
    {
        if (order.Type == CashOrderType.Incoming)
        {
            return BuildIncomingCashOrderPrintHtml(order, context);
        }

        if (order.Type == CashOrderType.Outgoing)
        {
            return BuildOutgoingCashOrderPrintHtml(order, context);
        }

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

    private static string BuildOutgoingCashOrderPrintHtml(CashOrder order, CashOrderPrintContext context)
    {
        var number = Html(order.Number);
        var organization = Html(context.OrganizationName);
        var receiver = Html(order.ReceiverName ?? order.PayerName ?? order.Client?.Name ?? string.Empty);
        var basis = Html(order.Basis ?? string.Empty);
        var appendix = Html(order.Appendix ?? string.Empty);
        var leaderPosition = Html(context.LeaderPosition);
        var leaderName = Html(context.LeaderName);
        var chiefAccountant = Html(context.ChiefAccountantName);
        var cashier = Html(context.CashierName);
        var fontCss = BuildCashOrderFontCss();
        var amountRubKop = Html(FormatOutgoingCashOrderRubKop(order.Amount));
        var amountWords = Html(AmountRublesToWordsRu(order.Amount));
        var kopecks = Html(FormatKopecks(order.Amount));
        var month = GetRussianMonthGenitive(order.Date.Month);
        var numberClass = order.Number.Length > 32
            ? " number-very-compact"
            : order.Number.Length > 16
                ? " number-compact"
                : string.Empty;
        var amountClass = amountRubKop.Length > 10 ? " amount-compact" : string.Empty;
        var cancelNotice = order.Status == CashOrderStatus.Cancelled
            ? $"""<div class="cancelled">Документ отменён{(order.CancelledAt.HasValue ? $" {order.CancelledAt:dd.MM.yyyy HH:mm}" : string.Empty)}{(string.IsNullOrWhiteSpace(order.CancelledReason) ? string.Empty : $": {Html(order.CancelledReason)}")}</div>"""
            : string.Empty;

        return $$"""
<!doctype html>
<html lang="ru">
<head>
  <meta charset="utf-8" />
  <title>Расходный кассовый ордер {{number}}</title>
  <style>
    {{fontCss}}
    @page { size: A4 portrait; margin: 0; }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      color: #333;
      font-family: "Cash Order Arial", Arial, sans-serif;
      font-size: 10px;
      line-height: 1.15;
      background: #fff;
      -webkit-print-color-adjust: exact !important;
      print-color-adjust: exact !important;
    }
    .ko2 { position: relative; width: 195.5mm; min-height: 120mm; margin: 18mm 0 0 7mm; }
    .top-note { margin-left: 123.45mm; margin-bottom: 8.45mm; font-size: 6pt; line-height: 1.05; transform: scaleX(.957); transform-origin: left top; }
    .header-row { display: block; padding-right: 45.05mm; padding-bottom: 5.5mm; transform: translateY(-.73mm); }
    .header-row .org-line { min-height: 18px; text-align: center; }
    .header-row .org-line > span { position: relative; top: -1px; left: 2px; font-size: 9.3px; }
    .header-row .hint { line-height: .5; }
    .org-line { border-bottom: 2px solid #111; min-height: 18px; padding: 0 6px 2px; }
    .hint { color: #333; font-size: 6px; text-align: center; }
    .code-box, .doc-meta, .ledger { border-collapse: collapse; width: 100%; table-layout: fixed; }
    .code-box td, .doc-meta td, .ledger th, .ledger td { border: 1px solid #111; padding: 1.5mm 1mm; text-align: center; vertical-align: middle; }
    .code-box { position: absolute; top: 5.55mm; right: -.25mm; width: 49.5mm; }
    .code-box td { padding: 0 .75mm; }
    .code-box td:first-child { width: 20mm; border: 0; text-align: right; padding-right: 6px; white-space: nowrap; }
    .code-box td:last-child { width: 29.5mm; font-weight: 400; }
    .code-box tr:nth-child(1), .code-box tr:nth-child(2), .code-box tr:nth-child(3) { height: 4.4mm; }
    .code-box tr:nth-child(4) { height: 5.25mm; }
    .title { position: absolute; top: 32.5mm; left: 35.4mm; width: 92mm; margin: 0; text-align: center; white-space: nowrap; font-size: 10pt; line-height: 1.05; font-weight: 400; transform: scaleX(.97); transform-origin: center top; text-transform: uppercase; }
    .doc-meta { position: absolute; top: 27.25mm; right: -.25mm; width: 59.25mm; margin: 0; font-size: 7px; }
    .doc-meta td { padding: 0 .75mm; line-height: 1; }
    .doc-meta tr:nth-child(1) { height: 3.97mm; }
    .doc-meta tr:nth-child(2) { height: 5.17mm; }
    .doc-meta .value { font-size: 9px; font-weight: 700; white-space: nowrap; }
    .doc-meta .number-compact { font-size: 6px; line-height: 1; white-space: nowrap; letter-spacing: -.1px; }
    .doc-meta .number-very-compact { font-size: 3px; line-height: 1; white-space: nowrap; letter-spacing: -.25px; }
    .ledger { position: absolute; top: 42.75mm; left: 0; margin: 0; font-size: 7px; }
    .ledger th, .ledger td { padding: .6mm 1mm; }
    .ledger th { font-weight: 400; }
    .ledger .sum { font-size: 8px; white-space: nowrap; }
    .ledger .sum.amount-compact { font-size: 5px; letter-spacing: -.15px; }
    .ledger tr:nth-child(1) { height: 4.5mm; }
    .ledger tr:nth-child(2) { height: 7.7mm; }
    .ledger tr:nth-child(3) { height: 4.5mm; }
    .line-field { display: grid; grid-template-columns: auto 1fr; gap: 2mm; align-items: end; margin-top: 2mm; font-size: 8px; }
    .line { min-height: 4mm; border-bottom: 1px solid #111; padding: 0 1.5mm .4mm; }
    .long-line { margin-top: 1.5mm; min-height: 4mm; border-bottom: 1px solid #111; }
    .amount-words { display: grid; grid-template-columns: auto 1fr auto 15mm auto; gap: 2mm; align-items: end; margin-top: 2mm; font-size: 8px; }
    .kopecks { text-align: center; }
    .sign-row { display: grid; grid-template-columns: 25mm 40.5mm 40.5mm; gap: 5.8mm; align-items: end; margin-top: 3mm; font-size: 8px; }
    .leader-row { display: block; }
    .leader-row > div:nth-child(2), .leader-row > div:nth-child(3), .leader-row > div:nth-child(4) { position: absolute; top: 0; }
    .leader-row > div:nth-child(2) { left: 37.8mm; width: 42.1mm; }
    .leader-row > div:nth-child(3) { left: 86.1mm; width: 42.1mm; }
    .leader-row > div:nth-child(4) { left: 134.45mm; width: 42.35mm; }
    .sign-line { border-bottom: 1px solid #111; min-height: 4.4mm; text-align: center; padding-bottom: .4mm; }
    .sign-label { font-size: 6px; text-align: center; color: #333; margin-top: .5mm; }
    .rko-fixed { position: absolute; left: 0; width: 100%; margin: 0; }
    .rko-receiver { top: 62mm; grid-template-columns: 8mm 1fr; }
    .rko-receiver-hint { position: absolute; top: 66.1mm; left: 0; width: 100%; }
    .rko-basis { top: 68.75mm; grid-template-columns: 12.5mm 1fr; }
    .rko-amount-words { top: 74.8mm; grid-template-columns: 8mm 1fr; }
    .rko-amount-hint { position: absolute; top: 79mm; left: 0; width: 100%; }
    .rko-amount-digits { top: 81.85mm; display: grid; grid-template-columns: 1fr auto 33mm auto; gap: 2mm; align-items: end; padding-right: 4mm; font-size: 8px; }
    .rko-appendix { top: 87.1mm; }
    .rko-appendix-blank { top: 94.38mm; border-top: 1px solid #111; }
    .rko-leader { top: 95.3mm; }
    .rko-chief { top: 102.1mm; display: block; }
    .rko-chief > div:nth-child(2), .rko-chief > div:nth-child(3) { position: absolute; top: 0; }
    .rko-chief > div:nth-child(2) { left: 30.1mm; width: 40.4mm; }
    .rko-chief > div:nth-child(3) { left: 76.35mm; width: 40.4mm; }
    .rko-received-words { top: 110mm; display: grid; grid-template-columns: auto 1fr; gap: 2mm; align-items: end; font-size: 8px; }
    .rko-received-hint { position: absolute; top: 116.6mm; left: 0; width: 100%; }
    .rko-received-digits { top: 117.1mm; display: grid; grid-template-columns: 1fr auto 33mm auto; gap: 2mm; align-items: end; padding-right: 4mm; font-size: 8px; }
    .received-date { top: 123.1mm; width: 160mm; height: 4mm; display: block; font-size: 8px; }
    .received-date > * { position: absolute; bottom: 0; }
    .received-date > span:nth-child(1) { left: 0; }
    .received-date > div:nth-child(2) { left: 3.75mm; width: 10.25mm; }
    .received-date > span:nth-child(3) { left: 15mm; }
    .received-date > div:nth-child(4) { left: 20mm; width: 35.5mm; }
    .received-date > div:nth-child(5) { left: 57.5mm; width: 13.5mm; }
    .received-date > span:nth-child(6) { left: 72.5mm; }
    .received-date > span:nth-child(7) { left: 109mm; }
    .received-date > div:nth-child(8) { left: 120mm; width: 39.25mm; }
    .rko-amount-digits > .line:first-child, .rko-received-digits > .line:first-child { margin-right: 1.8mm; }
    .rko-identity { top: 128.2mm; }
    .identity-note { position: absolute; left: 10mm; right: 0; margin: 0; font-size: 6px; text-align: center; }
    .identity-note-primary { top: 132.45mm; }
    .identity-note-secondary { top: 139.45mm; }
    .rko-identity-blank { top: 135.25mm; min-height: 4mm; border-bottom: 1px solid #111; }
    .rko-cashier { top: 142.3mm; grid-template-columns: 18.5mm 40mm 40mm; gap: 5.3mm; }
    .rko-basis > span { position: relative; top: -3.75pt; }
    .rko-receiver .line, .rko-amount-words .line { font-size: 9px; }
    .rko-receiver-hint, .rko-amount-hint { font-size: 7px; }
    .rko-appendix { font-size: 6px; }
    .rko-appendix > span { position: relative; top: -1.5pt; }
    .rko-identity > span { position: relative; top: -3pt; }
    .rko-leader > div:first-child, .rko-chief > div:first-child { position: relative; top: 1.5pt; }
    .rko-leader .sign-line, .rko-chief .sign-line { padding-bottom: 0; }
    .rko-leader, .rko-chief, .rko-cashier { font-size: 9px; }
    .rko-leader .positioned-text, .rko-chief .positioned-text { position: relative; top: 1.5pt; }
    .rko-cashier > div:first-child, .rko-cashier .positioned-text { position: relative; top: 1.5pt; }
    .rko-cashier > div:first-child { position: absolute; left: 0; top: 1.5pt; transform: translateY(-.5mm); }
    .rko-cashier > div:nth-child(2) { grid-column: 2; }
    .rko-cashier > div:nth-child(3) { grid-column: 3; }
    .rko-cashier .sign-label { position: relative; top: -.4mm; }
    .rko-received-words .line { text-align: center; font-size: 9px; }
    .identity-note { font-size: 7px; }
    .ko2 > .cancelled { position: absolute; top: 154mm; left: 0; width: 100%; margin: 0; border: 1px solid #111; padding: 1.5mm 2mm; font-size: 7px; line-height: 1.15; font-weight: 700; }
  </style>
</head>
<body>
  <main class="ko2">
    <div hidden><span class="word-leader-position">{{leaderPosition}}</span><span class="word-leader-name">{{leaderName}}</span><span class="word-chief">{{chiefAccountant}}</span><span class="word-cashier">{{cashier}}</span></div>
    <div class="top-note">
      <div>Унифицированная форма № КО-2</div>
      <div>Утверждена постановлением Госкомстата</div>
      <div>России от 18.08.98 № 88</div>
    </div>
    <div class="header-row">
      <div class="org-line"><span>{{organization}}</span></div>
      <div class="hint">(организация)</div>
      <div class="org-line"></div>
      <div class="hint">(структурное подразделение)</div>
    </div>
    <table class="code-box">
      <tr><td>Код</td><td></td></tr>
      <tr><td>Форма по ОКУД</td><td>0310002</td></tr>
      <tr><td>по ОКПО</td><td></td></tr>
      <tr><td></td><td></td></tr>
    </table>
    <h1 class="title">Расходный кассовый ордер</h1>
    <table class="doc-meta">
      <tr><td>Номер документа</td><td>Дата составления</td></tr>
      <tr><td class="value{{numberClass}}">{{number}}</td><td class="value">{{order.Date:dd.MM.yyyy}}</td></tr>
    </table>
    {{cancelNotice}}
    <table class="ledger">
      <colgroup><col style="width:12mm"><col style="width:29.5mm"><col style="width:41mm"><col style="width:39.5mm"><col style="width:14mm"><col style="width:23mm"><col style="width:23mm"><col style="width:13.75mm"></colgroup>
      <tr>
        <th colspan="4">Дебет</th>
        <th rowspan="2">Кредит</th>
        <th rowspan="2">Сумма,<br>руб. коп.</th>
        <th rowspan="2">Код целевого<br>назначения</th>
        <th rowspan="2"></th>
      </tr>
      <tr>
        <th style="width:25mm;">код структурного<br>подразделения</th>
        <th style="width:30mm;">корреспондирующий<br>счет, субсчет</th>
        <th style="width:25mm;">код аналитического<br>учета</th>
        <th style="width:18mm;">&nbsp;</th>
      </tr>
      <tr><td>&nbsp;</td><td>&nbsp;</td><td>&nbsp;</td><td>&nbsp;</td><td>&nbsp;</td><td class="sum{{amountClass}}">{{amountRubKop}}</td><td>&nbsp;</td><td>&nbsp;</td></tr>
    </table>
    <div class="line-field rko-fixed rko-receiver"><span>Выдать</span><div class="line">{{receiver}}</div></div>
    <div class="hint rko-receiver-hint">(фамилия, имя, отчество)</div>
    <div class="line-field rko-fixed rko-basis"><span>Основание</span><div class="line">{{basis}}</div></div>
    <div class="line-field rko-fixed rko-amount-words"><span>Сумма</span><div class="line">{{amountWords}}</div></div>
    <div class="hint rko-amount-hint">(прописью)</div>
    <div class="rko-fixed rko-amount-digits"><div class="line"></div><span>руб.</span><div class="line kopecks">{{kopecks}}</div><span>коп.</span></div>
    <div class="line-field rko-fixed rko-appendix"><span>Приложение</span><div class="line">{{appendix}}</div></div>
    <div class="rko-fixed rko-appendix-blank"></div>
    <div class="sign-row leader-row rko-fixed rko-leader">
      <div>Руководитель организации</div>
      <div><div class="sign-line"><span class="positioned-text">{{leaderPosition}}</span></div><div class="sign-label">(должность)</div></div>
      <div><div class="sign-line"></div><div class="sign-label">(подпись)</div></div>
      <div><div class="sign-line"><span class="positioned-text">{{leaderName}}</span></div><div class="sign-label">(расшифровка подписи)</div></div>
    </div>
    <div class="sign-row rko-fixed rko-chief">
      <div>Главный бухгалтер</div>
      <div><div class="sign-line"></div><div class="sign-label">(подпись)</div></div>
      <div><div class="sign-line"><span class="positioned-text">{{chiefAccountant}}</span></div><div class="sign-label">(расшифровка подписи)</div></div>
    </div>
    <div class="rko-fixed rko-received-words"><span>Получил</span><div class="line">{{amountWords}}</div></div>
    <div class="hint rko-received-hint">(сумма прописью)</div>
    <div class="rko-fixed rko-received-digits"><div class="line"></div><span>руб.</span><div class="line kopecks">{{kopecks}}</div><span>коп.</span></div>
    <div class="received-date rko-fixed"><span>«</span><div class="line">{{order.Date:dd}}</div><span>»</span><div class="line">{{month}}</div><div class="line">{{order.Date:yyyy}}</div><span>г.</span><span>Подпись</span><div class="line"></div></div>
    <div class="line-field rko-fixed rko-identity"><span>По</span><div class="line"></div></div>
    <div class="identity-note identity-note-primary">(наименование, номер, дата и место выдачи документа,</div>
    <div class="rko-fixed rko-identity-blank"></div>
    <div class="identity-note identity-note-secondary">удостоверяющего личность получателя)</div>
    <div class="sign-row rko-fixed rko-cashier">
      <div>Выдал кассир</div>
      <div><div class="sign-line"></div><div class="sign-label">(подпись)</div></div>
      <div><div class="sign-line"><span class="positioned-text">{{cashier}}</span></div><div class="sign-label">(расшифровка подписи)</div></div>
    </div>
  </main>
</body>
</html>
""";
    }

    private static string BuildCashBookPrintHtml(CashBookResponseDto cashBook)
    {
        var accountText = string.IsNullOrWhiteSpace(cashBook.AccountName)
            ? "Все кассы"
            : cashBook.AccountName;
        var rows = cashBook.Days.Count == 0
            ? """
              <tr><td colspan="6" class="empty">За выбранный период движений кассы нет.</td></tr>
              """
            : string.Join(Environment.NewLine, cashBook.Days.Select(day => $$"""
              <tr>
                <td>{{day.Date:dd.MM.yyyy}}</td>
                <td class="num">{{FormatRegisterAmount(day.OpeningBalance, cashBook.Currency, cashBook.CurrencyStatus)}}</td>
                <td class="num income">{{FormatRegisterAmount(day.Income, cashBook.Currency, cashBook.CurrencyStatus)}}</td>
                <td class="num expense">{{FormatRegisterAmount(day.Expense, cashBook.Currency, cashBook.CurrencyStatus)}}</td>
                <td class="num">{{FormatRegisterAmount(day.ClosingBalance, cashBook.Currency, cashBook.CurrencyStatus)}}</td>
                <td class="num">ПКО: {{day.IncomingCount}}, РКО: {{day.OutgoingCount}}</td>
              </tr>
              """));

        return $$"""
<!doctype html>
<html lang="ru">
<head>
  <meta charset="utf-8" />
  <title>Кассовая книга {{cashBook.From:dd.MM.yyyy}} - {{cashBook.To:dd.MM.yyyy}}</title>
  {{(cashBook.MoneyAccountId.HasValue ? $"<meta name=\"payplanner-account-id\" content=\"{cashBook.MoneyAccountId.Value}\">" : "")}}
  {{BuildRegisterPrintStyles()}}
</head>
<body>
  <main class="document">
    <header>
      <div class="eyebrow">Касса</div>
      <h1>Кассовая книга</h1>
      <div class="period">Период: {{cashBook.From:dd.MM.yyyy}} - {{cashBook.To:dd.MM.yyyy}}</div>
      <div class="period">Касса: {{Html(accountText)}}</div>
      {{(cashBook.MoneyAccountId.HasValue ? "<div class=\"period\">Только ордера с подтверждённой кассой; неподтверждённые ордера в остатки не входят.</div>" : "")}}
    </header>

    <section class="summary">
      <div><span>Остаток на начало</span><strong>{{FormatRegisterAmount(cashBook.OpeningBalance, cashBook.Currency, cashBook.CurrencyStatus)}}</strong></div>
      <div><span>Приход</span><strong class="income">{{FormatRegisterAmount(cashBook.Income, cashBook.Currency, cashBook.CurrencyStatus)}}</strong><small>ПКО: {{cashBook.IncomingCount}}</small></div>
      <div><span>Расход</span><strong class="expense">{{FormatRegisterAmount(cashBook.Expense, cashBook.Currency, cashBook.CurrencyStatus)}}</strong><small>РКО: {{cashBook.OutgoingCount}}</small></div>
      <div><span>Остаток на конец</span><strong>{{FormatRegisterAmount(cashBook.ClosingBalance, cashBook.Currency, cashBook.CurrencyStatus)}}</strong></div>
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

    private static string BuildRegisterPrintHtml(CashOrderRegisterResponseDto register, DateTime? from, DateTime? to, string? accountName)
    {
        var fromText = from?.Date.ToString("dd.MM.yyyy") ?? "не задано";
        var toText = to?.Date.ToString("dd.MM.yyyy") ?? "не задано";
        var accountText = string.IsNullOrWhiteSpace(accountName) ? "Все кассы" : accountName.Trim();
        var rows = register.Items.Count == 0
            ? """
              <tr><td colspan="8" class="empty">За выбранный период кассовых ордеров нет.</td></tr>
              """
            : string.Join(Environment.NewLine, register.Items.Select((order, index) =>
            {
                var typeLabel = order.Type == CashOrderType.Incoming ? "ПКО" : "РКО";
                var statusLabel = order.Status == CashOrderStatus.Cancelled
                    ? "Отменён"
                    : order.Status == CashOrderStatus.Draft
                        ? "Черновик"
                        : "Проведён";
                var amountClass = order.Type == CashOrderType.Incoming ? "income" : "expense";
                return $$"""
              <tr>
                <td class="num">{{index + 1}}</td>
                <td>{{order.Date:dd.MM.yyyy}}</td>
                <td>{{Html(order.Number)}}</td>
                <td>{{typeLabel}}</td>
                <td>{{statusLabel}}</td>
                <td class="num {{amountClass}}">{{FormatCashOrderAmount(order.Amount, order.Currency)}}</td>
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
  {{(register.MoneyAccountId.HasValue ? $"<meta name=\"payplanner-account-id\" content=\"{register.MoneyAccountId.Value}\">" : "")}}
  {{BuildRegisterPrintStyles()}}
  <style>
    thead { display: table-header-group; }
    tbody tr {
      break-inside: avoid-page !important;
      page-break-inside: avoid !important;
    }
    @media print {
      table { font-size: 10px; }
      th, td { padding: 4px 5px; line-height: 1.15; }
      th:nth-child(1), td:nth-child(1) { width: 4%; }
      th:nth-child(2), td:nth-child(2) { width: 9%; }
      th:nth-child(3), td:nth-child(3) { width: 12%; }
      th:nth-child(4), td:nth-child(4) { width: 6%; }
      th:nth-child(5), td:nth-child(5) { width: 9%; }
      th:nth-child(6), td:nth-child(6) { width: 12%; }
      th:nth-child(7), td:nth-child(7) { width: 19%; }
      th:nth-child(8), td:nth-child(8) { width: 29%; }
    }
  </style>
</head>
<body>
  <main class="document">
    <header>
      <div class="eyebrow">Касса</div>
      <h1>Журнал регистрации кассовых документов</h1>
      <div class="period">Период: {{fromText}} - {{toText}}</div>
      <div class="period">Касса: {{Html(accountText)}}</div>
      {{(register.MoneyAccountId.HasValue ? "<div class=\"period\">Только ордера с подтверждённой кассой; неподтверждённые ордера не включены.</div>" : "")}}
    </header>

    <section class="summary">
      <div><span>Приходные ордера</span><strong class="income">{{register.IncomingCount}} на {{FormatRegisterAmount(register.IncomingAmount, register.Currency, register.CurrencyStatus)}}</strong></div>
      <div><span>Расходные ордера</span><strong class="expense">{{register.OutgoingCount}} на {{FormatRegisterAmount(register.OutgoingAmount, register.Currency, register.CurrencyStatus)}}</strong></div>
      <div><span>Черновиков</span><strong>{{register.DraftCount}}</strong></div>
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

    private static string FormatRegisterAmount(decimal? amount, string? currency, string status)
        => !amount.HasValue ? "—" : status == "empty"
            ? amount.Value.ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"))
            : FormatCashOrderAmount(amount.Value, currency);

    private static string FormatCashOrderAmount(decimal amount, string? currency)
    {
        var label = string.IsNullOrWhiteSpace(currency) ? "валюта не указана"
            : string.Equals(currency.Trim(), "RUB", StringComparison.OrdinalIgnoreCase) ? "руб." : currency.Trim().ToUpperInvariant();
        return $"{amount.ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"))} {Html(label)}";
    }

    private static string FormatCashOrderRubKop(decimal value)
        => $"{FormatWholeRubles(value)}-{FormatKopecks(value)}";

    private static string FormatOutgoingCashOrderRubKop(decimal value)
        => Math.Abs(value)
            .ToString("#,##0.00", System.Globalization.CultureInfo.InvariantCulture)
            .Replace(',', ' ');

    private static string FormatWholeRubles(decimal value)
        => Math.Truncate(Math.Round(Math.Abs(value), 2, MidpointRounding.AwayFromZero))
            .ToString("0", System.Globalization.CultureInfo.InvariantCulture);

    private static string ShortCashOrderSigner(string? value)
    {
        var parts = (value ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length >= 3
            ? $"{parts[0]} {parts[1][0]}.{parts[2][0]}."
            : string.Join(' ', parts);
    }

    private static CashOrderPerson? ParseCashOrderPerson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<CashOrderPerson>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ShortCashOrderPerson(CashOrderPerson? person, string? fallback)
    {
        if (person is null || string.IsNullOrWhiteSpace(person.LastName))
        {
            return ShortCashOrderSigner(fallback);
        }

        var initials = new[]
        {
            string.IsNullOrWhiteSpace(person.FirstName) ? null : $"{char.ToUpperInvariant(person.FirstName.Trim()[0])}.",
            person.NoMiddleName || string.IsNullOrWhiteSpace(person.MiddleName)
                ? null
                : $"{char.ToUpperInvariant(person.MiddleName.Trim()[0])}.",
        };
        return $"{person.LastName.Trim()} {string.Join(' ', initials.Where(static item => item is not null))}".Trim();
    }

    private static string BuildCashOrderTaxText(CashOrder order, bool? vatWithoutVat)
    {
        if (vatWithoutVat != false)
        {
            return "Без налога (НДС) 0,00 руб.";
        }

        var explicitVat = order.MoneyOperation?.VatAmount;
        var vatRate = order.MoneyOperation?.VatRate;
        if (!explicitVat.HasValue && !vatRate.HasValue)
        {
            return "Без налога (НДС) 0,00 руб.";
        }

        var vatAmount = explicitVat.HasValue
            ? Math.Abs(explicitVat.Value)
            : vatRate > 0m
                ? Math.Round(Math.Abs(order.Amount) * vatRate.Value / (100m + vatRate.Value), 2, MidpointRounding.AwayFromZero)
                : 0m;
        return $"НДС {vatAmount.ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"))} руб.";
    }

    private sealed record CashOrderPrintContext(
        string OrganizationName,
        string LeaderPosition,
        string LeaderName,
        string ChiefAccountantName,
        string CashierName,
        string TaxText);

    private sealed class CashOrderPerson
    {
        public string? Position { get; set; }
        public string? LastName { get; set; }
        public string? FirstName { get; set; }
        public string? MiddleName { get; set; }
        public bool NoMiddleName { get; set; }
    }

    private static string BuildCashOrderFontCss()
    {
        var assembly = typeof(CashOrdersController).Assembly;
        static string ReadFont(System.Reflection.Assembly assembly, string resourceName)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Встроенный шрифт {resourceName} не найден.");
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return Convert.ToBase64String(memory.ToArray());
        }

        var regular = ReadFont(assembly, "PayPlanner.Fonts.LiberationSans-Regular.ttf");
        var bold = ReadFont(assembly, "PayPlanner.Fonts.LiberationSans-Bold.ttf");
        return $"@font-face {{ font-family:'Cash Order Arial'; src:url(data:font/ttf;base64,{regular}) format('truetype'); font-weight:400; }} "
            + $"@font-face {{ font-family:'Cash Order Arial'; src:url(data:font/ttf;base64,{bold}) format('truetype'); font-weight:700; }}";
    }

    private static string FormatKopecks(decimal value)
    {
        var rounded = Math.Round(Math.Abs(value), 2, MidpointRounding.AwayFromZero);
        var rubles = Math.Truncate(rounded);
        return ((int)((rounded - rubles) * 100m)).ToString("00");
    }

    private static string AmountRublesToWordsRu(decimal value)
    {
        var rounded = Math.Round(Math.Abs(value), 2, MidpointRounding.AwayFromZero);
        var rubles = (long)Math.Truncate(rounded);
        return NumberToWordsRu(rubles, feminine: false);
    }

    private static string GetRussianMonthGenitive(int month)
        => month switch
        {
            1 => "января",
            2 => "февраля",
            3 => "марта",
            4 => "апреля",
            5 => "мая",
            6 => "июня",
            7 => "июля",
            8 => "августа",
            9 => "сентября",
            10 => "октября",
            11 => "ноября",
            12 => "декабря",
            _ => string.Empty,
        };

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
