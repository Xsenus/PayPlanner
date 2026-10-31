using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Payments;

namespace PayPlanner.Api.Controllers.V1;

[ApiController]
[Route("api/v1/payments")]
[Authorize]
public class PaymentsV1Controller : ControllerBase
{
    private readonly PaymentContext _db;
    public PaymentsV1Controller(PaymentContext db) => _db = db;

    private Task<DataVisibilityAccess?> ResolvePaymentsVisibilityAsync(CancellationToken ct)
        => _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Payments, ct);

    private ActionResult MissingPaymentsVisibilityResult()
        => User.GetUserId().HasValue ? Forbid() : Unauthorized();

    private async Task<IActionResult?> RequirePaymentsPermissionAsync(
        Func<RolePermissionValues, bool> isAllowed,
        CancellationToken ct)
        => await _db.HasUserSectionPermissionAsync(User, RolePermissionCatalog.Payments, isAllowed, ct)
            ? null
            : Forbid();

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int? clientId,
        [FromQuery] int? caseId,
        [FromQuery] bool excludeInstallmentParents = true,
        [FromQuery] string? clientRole = null,
        [FromQuery] string? search = null,
        [FromQuery] string? sortBy = null,
        [FromQuery] string? sortDir = null,
        CancellationToken ct = default)
    {
        var visibility = await ResolvePaymentsVisibilityAsync(ct);
        if (visibility is null) return MissingPaymentsVisibilityResult();

        var q = _db.Payments.AsQueryable().WithPaymentIncludes()
            .ApplyPaymentVisibility(visibility)
            .ExcludeInstallmentParentInvoices(_db, excludeInstallmentParents)
            .ApplyPaymentFilters(from, to, clientId, caseId, search, clientRole)
            .ApplyPaymentSort(sortBy, sortDir);

        var items = await q.AsNoTracking().ToListAsync(ct);
        PaymentDisplayNameService.FillCounterpartyNames(items);
        return Ok(items);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id, CancellationToken ct)
    {
        var visibility = await ResolvePaymentsVisibilityAsync(ct);
        if (visibility is null) return MissingPaymentsVisibilityResult();

        var entity = await _db.Payments
            .WithPaymentIncludes()
            .ApplyPaymentVisibility(visibility)
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id, ct);
        if (entity is null)
        {
            return NotFound();
        }

        PaymentDisplayNameService.FillCounterpartyNames([entity]);
        return Ok(entity);
    }

    [HttpPost]
    public Task<IActionResult> Create([FromBody] Payment model, CancellationToken ct = default)
        => VersionedPaymentWriteBoundary.RunAsync(this, _db, null, model, creating: true, deleting: false, changesInvoice: false,
            scope => CreateCoreAsync(model, scope, ct), ct);

    private async Task<IActionResult> CreateCoreAsync(Payment model, VersionedPaymentWriteBoundary.Scope scope, CancellationToken ct)
    {
        if (model.IncomeTypeId.HasValue)
        {
            var it = await _db.IncomeTypes.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == model.IncomeTypeId.Value);
            if (it is null) return BadRequest("Unknown IncomeTypeId");
            if (it.PaymentType != model.Type)
                return BadRequest("IncomeType.PaymentType mismatches payment.Type");
        }

        var createValidationError = await ValidatePaymentAttributionAsync(model, ct);
        if (createValidationError is not null) return createValidationError;

        model.Account = string.IsNullOrWhiteSpace(model.Account) ? null : model.Account.Trim();
        var now = DateTime.UtcNow;
        PaymentAdvanceReviewService.ApplyAutomaticReviewState(model, now, HttpContext);
        _db.Payments.Add(model);
        await _db.SaveChangesAsync(ct);
        await PaymentAdvanceReviewService.SyncBalanceAsync(_db, model, now, HttpContext, ct);
        await InvoicePaymentStateService.RefreshAfterPaymentChangeAsync(_db, model, now, ct);
        await _db.SaveChangesAsync(ct);
        return Created($"/api/v1/payments/{model.Id}", model);
    }

    [HttpPut("{id:int}")]
    public Task<IActionResult> Update(int id, [FromBody] Payment model, CancellationToken ct = default)
        => VersionedPaymentWriteBoundary.RunAsync(this, _db, id, model, creating: false, deleting: false, changesInvoice: false,
            scope => UpdateCoreAsync(id, model, scope, ct), ct);

    private async Task<IActionResult> UpdateCoreAsync(int id, Payment model, VersionedPaymentWriteBoundary.Scope scope, CancellationToken ct)
    {
        var e = await _db.Payments.FindAsync(id);
        if (e is null) return NotFound();
        var originalInvoiceId = e.InvoiceId;

        if (model.IncomeTypeId.HasValue)
        {
            var it = await _db.IncomeTypes.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == model.IncomeTypeId.Value);
            if (it is null) return BadRequest("Unknown IncomeTypeId");
            if (it.PaymentType != model.Type)
                return BadRequest("IncomeType.PaymentType mismatches payment.Type");
        }

        e.Date = model.Date;
        e.Amount = model.Amount;
        e.Type = model.Type;
        e.Status = model.Status;
        e.Description = model.Description;
        e.IsPaid = model.IsPaid;
        e.PaidDate = model.PaidDate;
        e.Notes = model.Notes;
        e.ClientId = model.ClientId;
        e.ExpenseClientId = model.ExpenseClientId;
        e.ClientCaseId = model.ClientCaseId;
        e.DealTypeId = model.DealTypeId;
        e.IncomeTypeId = model.IncomeTypeId;
        e.PaymentSourceId = model.PaymentSourceId;
        e.PaymentStatusId = model.PaymentStatusId;
        e.Account = string.IsNullOrWhiteSpace(model.Account) ? null : model.Account.Trim();
        e.AccountDate = model.AccountDate;
        e.PaymentOrderNumber = model.PaymentOrderNumber;
        e.PaymentDocumentDate = model.PaymentDocumentDate;

        var updateValidationError = await ValidatePaymentAttributionAsync(e, ct);
        if (updateValidationError is not null) return updateValidationError;

        await InvoicePaymentStateService.RefreshAfterPaymentChangeAsync(_db, e, DateTime.UtcNow, ct);
        if (originalInvoiceId.HasValue
            && originalInvoiceId.Value > 0
            && originalInvoiceId != e.InvoiceId)
        {
            await InvoicePaymentStateService.RefreshInvoiceAggregateAsync(_db, originalInvoiceId.Value, DateTime.UtcNow, ct);
        }

        await _db.SaveChangesAsync(ct);
        return Ok(e);
    }

    [HttpDelete("{id:int}")]
    public Task<IActionResult> Delete(int id, CancellationToken ct = default)
        => VersionedPaymentWriteBoundary.RunAsync(this, _db, id, null, creating: false, deleting: true, changesInvoice: false,
            scope => DeleteCoreAsync(id, scope, ct), ct);

    private async Task<IActionResult> DeleteCoreAsync(int id, VersionedPaymentWriteBoundary.Scope scope, CancellationToken ct)
    {
        var e = await _db.Payments.FindAsync(id);
        if (e is null) return NotFound();
        var invoiceId = e.InvoiceId;
        _db.Payments.Remove(e);
        await _db.SaveChangesAsync(ct);
        if (invoiceId.HasValue && invoiceId.Value > 0)
        {
            await InvoicePaymentStateService.RefreshInvoiceAggregateAsync(_db, invoiceId.Value, DateTime.UtcNow, ct);
            await _db.SaveChangesAsync(ct);
        }

        return NoContent();
    }

    private async Task<ActionResult?> ValidatePaymentAttributionAsync(Payment payment, CancellationToken ct)
    {
        var clientsVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Clients, ct);
        var legalEntitiesVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.LegalEntities, ct);

        var hasClient = payment.ClientId.HasValue && payment.ClientId.Value > 0;
        var hasLegalEntity = payment.LegalEntityId.HasValue && payment.LegalEntityId.Value > 0;
        if (hasClient && hasLegalEntity)
        {
            return BadRequest("Payment must reference either clientId or legalEntityId, not both.");
        }

        if (hasClient)
        {
            var clientExists = await _db.Clients
                .AsNoTracking()
                .ApplyClientVisibility(clientsVisibility)
                .AnyAsync(client => client.Id == payment.ClientId!.Value, ct);
            if (!clientExists)
            {
                return BadRequest($"clientId {payment.ClientId!.Value} is not available");
            }
        }

        if (hasLegalEntity)
        {
            var legalEntityExists = await _db.LegalEntities
                .AsNoTracking()
                .ApplyLegalEntityVisibility(legalEntitiesVisibility)
                .AnyAsync(entity => entity.Id == payment.LegalEntityId!.Value, ct);
            if (!legalEntityExists)
            {
                return BadRequest($"legalEntityId {payment.LegalEntityId!.Value} is not available");
            }
        }

        var hasExpenseClient = payment.ExpenseClientId.HasValue && payment.ExpenseClientId.Value > 0;
        var hasExpenseLegalEntity = payment.ExpenseLegalEntityId.HasValue && payment.ExpenseLegalEntityId.Value > 0;
        if (hasExpenseClient && hasExpenseLegalEntity)
        {
            return BadRequest("Expense payment must reference either expenseClientId or expenseLegalEntityId, not both.");
        }

        if (payment.Type == PaymentType.Expense && hasExpenseClient)
        {
            var expenseClientExists = await _db.Clients
                .AsNoTracking()
                .ApplyClientVisibility(clientsVisibility)
                .AnyAsync(client => client.Id == payment.ExpenseClientId!.Value, ct);
            if (!expenseClientExists)
            {
                return BadRequest($"expenseClientId {payment.ExpenseClientId!.Value} is not available");
            }
        }

        if (payment.Type == PaymentType.Expense && hasExpenseLegalEntity)
        {
            var expenseLegalEntityExists = await _db.LegalEntities
                .AsNoTracking()
                .ApplyLegalEntityVisibility(legalEntitiesVisibility)
                .AnyAsync(entity => entity.Id == payment.ExpenseLegalEntityId!.Value, ct);
            if (!expenseLegalEntityExists)
            {
                return BadRequest($"expenseLegalEntityId {payment.ExpenseLegalEntityId!.Value} is not available");
            }
        }

        if (!payment.ClientCaseId.HasValue || payment.ClientCaseId.Value <= 0)
        {
            return null;
        }

        var caseOwnerClientId = payment.Type == PaymentType.Expense
            ? payment.ExpenseClientId
            : payment.ClientId;
        var caseOwnerLegalEntityId = payment.Type == PaymentType.Expense
            ? payment.ExpenseLegalEntityId
            : payment.LegalEntityId;
        var hasCaseOwnerClient = caseOwnerClientId.HasValue && caseOwnerClientId.Value > 0;
        var hasCaseOwnerLegalEntity = caseOwnerLegalEntityId.HasValue && caseOwnerLegalEntityId.Value > 0;
        if (hasCaseOwnerClient == hasCaseOwnerLegalEntity)
        {
            return payment.Type == PaymentType.Expense
                ? BadRequest("ExpenseClientId or expenseLegalEntityId is required when clientCaseId is specified for expense payments.")
                : BadRequest("ClientId or legalEntityId is required when clientCaseId is specified.");
        }

        var casesVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Cases, ct);
        var clientCase = await _db.ClientCases
            .AsNoTracking()
            .ApplyClientCaseVisibility(casesVisibility)
            .FirstOrDefaultAsync(item => item.Id == payment.ClientCaseId.Value, ct);
        if (clientCase is null)
        {
            return BadRequest($"clientCaseId {payment.ClientCaseId.Value} is not available");
        }

        if (hasCaseOwnerClient && clientCase.ClientId != caseOwnerClientId!.Value)
        {
            return payment.Type == PaymentType.Expense
                ? BadRequest($"clientCaseId {payment.ClientCaseId.Value} does not belong to expenseClientId {caseOwnerClientId.Value}")
                : BadRequest($"clientCaseId {payment.ClientCaseId.Value} does not belong to clientId {caseOwnerClientId.Value}");
        }

        if (hasCaseOwnerLegalEntity && clientCase.LegalEntityId != caseOwnerLegalEntityId!.Value)
        {
            return payment.Type == PaymentType.Expense
                ? BadRequest($"clientCaseId {payment.ClientCaseId.Value} does not belong to expenseLegalEntityId {caseOwnerLegalEntityId.Value}")
                : BadRequest($"clientCaseId {payment.ClientCaseId.Value} does not belong to legalEntityId {caseOwnerLegalEntityId.Value}");
        }

        return null;
    }
}
