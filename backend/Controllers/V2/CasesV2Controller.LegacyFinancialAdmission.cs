using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;

namespace PayPlanner.Api.Controllers.V2;

public partial class CasesV2Controller
{
    private async Task<(ActionResult? Blocked, IDbContextTransaction? Transaction)> BeginLegacyFinancialAsync(
        ClientCase clientCase, ClientCaseWorkOrder order, GeneratedFinancialDocumentKind kind, CancellationToken ct)
    {
        var tx = await _db.Database.BeginTransactionAsync(ct);
        var handedOff = false;
        try
        {
            if (!await GeneratedFinancialDocumentRegistry.LockSourceAsync(_db, clientCase.Id, order.Id, ct))
                return (NotFound(), null);
            if (await GuardLegacyFinancialSourceAsync(clientCase.Id, order.Id, kind, ct) is { } blocked)
                return (blocked, null);
            await new GeneratedFinancialClaims(_db).LockNumberScopeAsync(kind, ct);
            await _db.Entry(clientCase).ReloadAsync(ct);
            await _db.Entry(order).ReloadAsync(ct);
            // The initial editable-order read preceded the lock. Refresh its
            // lines in one query, retaining tracked originals for recalculation.
            var previousItems = order.Items.ToArray();
            if (previousItems.Any(item => _db.Entry(item).State != EntityState.Unchanged))
                throw new InvalidOperationException("Legacy financial admission cannot discard changed source lines.");
            var freshItems = await _db.ClientCaseWorkOrderItems.AsNoTracking()
                .Where(item => item.ClientCaseWorkOrderId == order.Id).ToListAsync(ct);
            foreach (var item in previousItems) _db.Entry(item).State = EntityState.Detached;
            order.Items = freshItems;
            _db.AttachRange(freshItems);
            handedOff = true;
            return (null, tx);
        }
        finally
        {
            if (!handedOff) await tx.DisposeAsync();
        }
    }

    private async Task<ActionResult?> GuardLegacyFinancialSourceAsync(int caseId, int orderId,
        GeneratedFinancialDocumentKind kind, CancellationToken ct)
    {
        if (_db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Legacy financial admission requires its source transaction.");
        if (_managedCaseUploads?.Enabled == true)
            return StatusCode(503, new { message = "Способ сохранения финансового документа изменился. Повторите ту же попытку." });
        if (await GuardFinancialLegacyAttemptAsync(caseId, ct) is { } blocked) return blocked;
        var actorId = User.GetUserId();
        if (actorId is not > 0) return Unauthorized();
        if (!await CaseDocumentUploadAuthorization.CanPublishAsync(_db, actorId.Value, caseId, ct)) return Forbid();
        var section = kind switch
        {
            GeneratedFinancialDocumentKind.Invoice => RolePermissionCatalog.Accounts,
            GeneratedFinancialDocumentKind.Act => RolePermissionCatalog.Acts,
            _ => RolePermissionCatalog.Contracts
        };
        if (await RequireSectionPermissionAsync(section, static p => p.CanCreate, ct) is { } forbidden) return forbidden;
        return await GeneratedFinancialDocumentRegistry.HasActiveAttemptAsync(_db, caseId, orderId, kind, ct)
            ? Conflict(new { message = "По заказ-наряду уже есть незавершённая попытка этого финансового документа. Завершите её или явно прекратите перед новой попыткой." })
            : null;
    }
}
