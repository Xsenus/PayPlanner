using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Companies;
using PayPlanner.Api.Services.Storage;

namespace PayPlanner.Api.Controllers.V2;

public partial class CasesV2Controller
{
    private bool TryReadWorkOrderActKey(out Guid? key)
    {
        key = null;
        var headers = HttpContext?.Request.Headers;
        if (headers is null || !headers.TryGetValue("X-Upload-Key", out var value)) return true;
        if (value.Count != 1 || !Guid.TryParse(value[0], out var parsed) || parsed == Guid.Empty) return false;
        key = parsed;
        return true;
    }

    private async Task<ActionResult<ClientCaseWorkOrderActDto>?> ReplayWorkOrderActAsync(int caseId, Guid key,
        string commandHash, CancellationToken ct)
    {
        var actorId = User.GetUserId();
        if (actorId is not > 0) return Unauthorized();
        var receipt = await _db.ActCreateReceipts.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ActorUserId == actorId && item.UploadKey == key, ct);
        int? publishedDocumentId = null;
        if (receipt is null)
        {
            // A managed publication can be replayed after the setting changes.
            // A pending capture remains protected by the existing storage guard.
            var managed = await _db.GeneratedFinancialDocumentAttempts.AsNoTracking()
                .SingleOrDefaultAsync(item => item.ClientCaseId == caseId && item.ActorId == actorId && item.UploadKey == key, ct);
            if (managed is null) return null;
            if (!managed.PublishedDocumentId.HasValue) return null;
            if (managed.Kind != "act" || managed.RequestSha256 != commandHash)
                return FinancialFailureResult("conflict", key);
            publishedDocumentId = managed.PublishedDocumentId;
            receipt = new ActCreateReceipt
            {
                ActorUserId = actorId.Value,
                UploadKey = key,
                Operation = "work-order",
                CaseId = caseId,
                WorkOrderId = managed.WorkOrderId,
                CommandSha256 = commandHash,
                ActId = managed.EntityId
            };
        }
        if (receipt.Operation != "work-order" || receipt.CaseId != caseId || receipt.CommandSha256 != commandHash)
            return Conflict(new { message = "Эта попытка относится к другой команде создания акта." });
        var source = await LoadEditableWorkOrderAsync(caseId, ct);
        if (source.Result is not null) return source.Result;
        var (clientCase, order) = source.Value;
        if (order.Id != receipt.WorkOrderId) return Conflict();
        if (await RequireSectionPermissionAsync(RolePermissionCatalog.Acts, permission => permission.CanView, ct) is { } forbidden)
            return forbidden;
        var visibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Acts, ct);
        if (visibility is null) return Forbid();
        var act = await _db.Acts.ApplyActVisibility(visibility).Include(item => item.LineItems)
            .SingleOrDefaultAsync(item => item.Id == receipt.ActId, ct);
        if (act is null)
            return await _db.Acts.AnyAsync(item => item.Id == receipt.ActId, ct) ? NotFound() : StatusCode(410);
        GeneratedFinancialDomainSnapshot? frozenDomain = null;
        if (publishedDocumentId.HasValue)
        {
            var registry = new GeneratedFinancialDocumentRegistry(_db);
            GeneratedFinancialCaptureResult capture;
            try
            {
                capture = await registry.CaptureAsync(caseId, order.Id, actorId.Value, User.GetDisplayName(), key,
                    GeneratedFinancialDocumentKind.Act, commandHash,
                    static (_, _, _, _) => throw new FinancialCaptureRejectedException(), ct);
            }
            catch (FinancialCaptureRejectedException)
            {
                return Conflict();
            }
            if (capture.Status != "recorded" || capture.Receipt is null)
                return FinancialFailureResult(capture.Status, key);
            frozenDomain = await registry.ReadDomainAsync(capture.Receipt.Generation, ct);
            if (frozenDomain is null) return Forbid();
        }
        return await CompleteLegacyWorkOrderActAsync(clientCase, order, act, key, ct,
            receipt.PublishedDocumentId ?? publishedDocumentId, frozenDomain);
    }

    private async Task<ActionResult<ClientCaseWorkOrderActDto>> CompleteLegacyWorkOrderActAsync(
        ClientCase clientCase, ClientCaseWorkOrder order, Act act, Guid? key, CancellationToken ct,
        int? publishedDocumentId = null, GeneratedFinancialDomainSnapshot? frozenDomain = null)
    {
        var documentKey = BuildWorkOrderActDocumentKey(act.Id);
        if (!publishedDocumentId.HasValue && key.HasValue)
        {
            var actorId = User.GetUserId();
            publishedDocumentId = await _db.ActCreateReceipts.AsNoTracking()
                .Where(item => item.ActorUserId == actorId && item.UploadKey == key.Value
                    && item.Operation == "work-order" && item.CaseId == clientCase.Id
                    && item.WorkOrderId == order.Id && item.ActId == act.Id)
                .Select(item => item.PublishedDocumentId).SingleOrDefaultAsync(ct);
        }
        ClientCaseDocument? document = null;
        if (publishedDocumentId.HasValue)
        {
            document = await _db.ClientCaseDocuments.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == publishedDocumentId.Value
                    && item.ClientCaseId == clientCase.Id && item.DocumentKey == documentKey, ct);
            if (document is null) return StatusCode(410);
        }
        else if (!key.HasValue)
        {
            document = await _db.ClientCaseDocuments.AsNoTracking()
                .Where(item => item.ClientCaseId == clientCase.Id && item.DocumentKey == documentKey)
                .OrderByDescending(item => item.Version).FirstOrDefaultAsync(ct);
        }
        if (document is null)
        {
            if (_managedCaseUploads?.Enabled == true)
                return StatusCode(503, new { message = "Акт сохранён, создание его файла ожидает прежнего способа сохранения.", actId = act.Id });
            if (key.HasValue && await GuardFinancialLegacyAttemptAsync(clientCase.Id, ct) is { } blocked) return blocked;
            var seller = await new DocumentSellerService(_db).ResolveAsync(act,
                await OwnCompanyRegistry.LoadDefaultAsync(_db, ct), freeze: false, ct);
            var clock = await ResolveCaseStatusClockAsync(ct);
            document = await SaveGeneratedCaseDocumentAsync(clientCase, documentKey, "Акт", "Черновик",
                $"case-{clientCase.Id}-act-{act.Id}.html",
                AppendFinancialSellerHtml(BuildWorkOrderActDocumentHtml(clientCase, order, act, clock.NowLocal), seller),
                "Generated", $"Сформирован документ акта №{act.Number} из заказ-наряда «{order.Title}».",
                $"Акт №{act.Number} из заказ-наряда «{order.Title}» сохранен в документы дела. Автор: {User.GetDisplayName()}.", ct,
                actCreate: key.HasValue ? (User.GetUserId()!.Value, key.Value, act.Id) : null);
        }
        if (key.HasValue) Response.Headers["X-Upload-Key"] = key.Value.ToString("D");
        return Created($"/acts/{act.Id}", new ClientCaseWorkOrderActDto
        {
            ActId = act.Id,
            Number = frozenDomain?.Number ?? act.Number,
            Date = frozenDomain?.Date ?? act.Date,
            Amount = frozenDomain?.Amount ?? act.Amount,
            DocumentId = document.Id,
            LineItemCount = frozenDomain?.Lines.Length ?? act.LineItems.Count,
            WorkOrder = MapWorkOrder(order),
        });
    }
}
