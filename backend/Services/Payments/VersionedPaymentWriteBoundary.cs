using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Payments;

// V1/V2 keep their payload and normalization rules; money is committed through
// the same lease and credit invariant as ordinary payment writers.
internal static class VersionedPaymentWriteBoundary
{
    internal sealed class Scope(Func<int, CancellationToken, Task> lockPayment)
    {
        private readonly HashSet<int> locked = [];
        public Func<Task>? AfterCommit { get; set; }
        public async Task LockPaymentAsync(int id, CancellationToken ct)
        {
            if (locked.Add(id)) await lockPayment(id, ct);
        }
    }

    public static async Task<IActionResult> RunAsync(ControllerBase controller, PaymentContext db, int? id, Payment? model,
        bool creating, bool deleting, bool changesInvoice, Func<Scope, Task<IActionResult>> write, CancellationToken ct, bool deferInvoiceValidation = false)
    {
        var actorId = controller.User.GetUserId();
        if (controller.User?.Identity?.IsAuthenticated != true || actorId is not > 0) return controller.Unauthorized();
        BalanceApplicationLease? lease = null; Scope? scope = null; IActionResult result;
        try
        {
            lease = await BalanceApplicationLease.BeginAsync(db, actorId.Value, ct);
            scope = new Scope(lease.LockPaymentAsync);
            using var credit = new PaymentCreditInvariantScope(db);
            if (db.Database.IsNpgsql()) await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'", ct);
            if (!await db.Users.AsNoTracking().AnyAsync(u => u.Id == actorId.Value && u.IsActive && u.IsApproved, ct)) return controller.Unauthorized();
            if (!await db.HasUserSectionPermissionAsync(controller.User, RolePermissionCatalog.Payments,
                permission => creating ? permission.CanCreate : deleting ? permission.CanDelete : permission.CanEdit, ct)) return controller.Forbid();
            var visibility = await db.ResolveDataVisibilityAsync(controller.User, RolePermissionCatalog.Payments, ct);
            if (visibility is null) return controller.Forbid();
            if (creating && model is not null && PaymentCreateInputRules.Validate(db, model) is { } inputError)
                return controller.BadRequest(new { message = inputError });
            Payment? current = null;
            Payment? original = null;
            if (id.HasValue)
            {
                if (!await db.Payments.AsNoTracking().ApplyPaymentVisibility(visibility).ApplyUnambiguousInvoiceLinks().AnyAsync(p => p.Id == id, ct)) return controller.NotFound();
                await lease.LockPaymentAsync(id.Value, ct);
                current = await db.Payments.SingleAsync(p => p.Id == id, ct);
                original = (Payment)db.Entry(current).CurrentValues.ToObject();
                if (await PaymentCreditInvariantScope.IsCancellationProtectedAsync(db, id.Value, ct))
                {
                    if (deleting || model is null || current.DocumentKind != DocumentKind.Payment
                        || InvoiceManagedPaymentRules.IsInvoiceManagedPayment(current)
                        || CancelledReceiptAnnotations.HasFinancialChanges(current, model)
                        || !string.IsNullOrEmpty(model.SystemNotes) && model.SystemNotes != current.SystemNotes)
                        return controller.BadRequest(new { message = PaymentCreditInvariantScope.CancellationProtectedMessage });
                    if (await ValidateInvoiceAsync(current.InvoiceId, false) is { } hiddenParent) return hiddenParent;
                    current.Description = model.Description ?? string.Empty; current.Notes = model.Notes ?? string.Empty;
                    await db.SaveChangesAsync(ct); await lease.CommitAsync(ct);
                    return controller.Ok(current);
                }
                var hasBalanceHistory = await db.ClientBalanceTransactions.AsNoTracking().AnyAsync(t => t.PaymentId == id, ct);
                if (hasBalanceHistory && (deleting || model is not null && (current.Type != model.Type
                    || current.ClientId != model.ClientId || current.LegalEntityId != model.LegalEntityId
                    || current.ExpenseClientId != model.ExpenseClientId || current.ExpenseLegalEntityId != model.ExpenseLegalEntityId
                    || changesInvoice && current.InvoiceId != model.InvoiceId)))
                    return controller.BadRequest(new { message = "По платежу есть история баланса. Изменение скрыло бы её основание; требуется сверка истории." });
                if (await ValidateInvoiceAsync(current.InvoiceId, true) is { } invalidSource) return invalidSource;
                if (await ValidateStructureAsync(db, current, model, deleting, ct) is { } structureError)
                    return controller.Conflict(new { message = structureError });
            }
            if (!deferInvoiceValidation && (creating || changesInvoice && model?.InvoiceId != current?.InvoiceId))
            {
                if (await ValidateInvoiceAsync(model?.InvoiceId, true) is { } invalidTarget) return invalidTarget;
            }
            var balanceBefore = id.HasValue ? await PaymentBalanceHistoryRules.CaptureOwnerBalanceAsync(db, id.Value, ct) : null;
            result = await write(scope);
            if (result is OkObjectResult or CreatedResult or NoContentResult)
            {
                if (original is not null && current is not null
                    && await PaymentRemainderWriteRules.ValidateUpdateAsync(db, original, current, ct) is { } normalizedError)
                    return controller.Conflict(new { message = normalizedError });
                if (!await credit.KeepsAppliedCreditAsync(ct)) return controller.BadRequest(new { message = PaymentCreditInvariantScope.RejectionMessage });
                if (!await PaymentBalanceHistoryRules.KeepsOwnerBalanceAsync(db, balanceBefore, ct))
                    return controller.BadRequest(new { message = "Изменение создало бы отрицательный остаток баланса. Требуется сверка оплаты." });
                await lease.CommitAsync(ct);
            }

            async Task<IActionResult?> ValidateInvoiceAsync(int? invoiceId, bool rejectClosed)
            {
                if (!invoiceId.HasValue) return null;
                var header = await db.Payments.AsNoTracking().ApplyPaymentVisibility(visibility).ApplyUnambiguousInvoiceLinks()
                    .Where(p => p.InvoiceId == invoiceId && p.DocumentKind == DocumentKind.Invoice)
                    .OrderBy(p => p.Status == PaymentStatus.Cancelled || p.InvoiceWorkflowStatus == InvoiceWorkflowStatus.Cancelled)
                    .ThenBy(p => p.Id).FirstOrDefaultAsync(ct);
                if (header is null)
                {
                    if (visibility.SeesAll && await db.Invoices.AnyAsync(i => i.Id == invoiceId && !i.Payments.Any(), ct)) return null;
                    return controller.NotFound();
                }
                if (header.Id != id) await lease.LockPaymentAsync(header.Id, ct);
                if (rejectClosed && await PaymentCreditInvariantScope.IsCancellationProtectedAsync(db, header.Id, ct))
                    return controller.BadRequest(new { message = PaymentCreditInvariantScope.CancellationProtectedMessage });
                if ((creating || changesInvoice && model?.InvoiceId != current?.InvoiceId && invoiceId == model?.InvoiceId) && model is not null
                    && (model.Type != header.Type || model.ClientId != header.ClientId || model.LegalEntityId != header.LegalEntityId
                        || model.ExpenseClientId != header.ExpenseClientId || model.ExpenseLegalEntityId != header.ExpenseLegalEntityId))
                    return controller.BadRequest(new { message = "Платёж и связанный счёт должны относиться к одному контрагенту и направлению." });
                return null;
            }
        }
        catch (BalanceSourceHistoryException error) { return controller.BadRequest(new { message = error.Message }); }
        catch (Exception error) when (BalanceApplicationLease.IsConflict(error)
            && (lease?.CommitStarted != true || BalanceApplicationLease.IsDefiniteDatabaseAbort(error)))
        { return controller.Conflict(new { message = "Платёж или права изменились параллельно. Обновите данные и повторите действие." }); }
        finally { if (lease is not null) await lease.DisposeAsync(); }
        if (scope?.AfterCommit is not null && result is OkObjectResult or CreatedResult or NoContentResult) await scope.AfterCommit();
        return result;
    }

    private static async Task<string?> ValidateStructureAsync(PaymentContext db, Payment current, Payment? model, bool deleting, CancellationToken ct)
    {
        if (!deleting && InvoiceManagedPaymentRules.IsInvoiceManagedPayment(current))
            return InvoiceManagedPaymentRules.UpdateBlockedMessage;

        var remainderError = deleting ? await PaymentRemainderWriteRules.ValidateDeleteAsync(db, current, ct)
            : model is not null ? await PaymentRemainderWriteRules.ValidateUpdateAsync(db, current, model, ct) : null;
        if (remainderError is not null) return remainderError;
        if (deleting && InvoiceManagedPaymentRules.IsInvoiceManagedPayment(current)
            && (current.DocumentKind == DocumentKind.Invoice || InvoiceManagedPaymentRules.IsGeneratedInvoiceRemainderPayment(current)))
            return InvoiceManagedPaymentRules.DeleteBlockedMessage;
        return null;
    }
}
