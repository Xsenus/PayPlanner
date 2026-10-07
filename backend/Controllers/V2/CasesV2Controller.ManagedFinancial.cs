using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Services.Storage;

namespace PayPlanner.Api.Controllers.V2;

public partial class CasesV2Controller
{
    private async Task<ActionResult?> GuardFinancialLegacyAttemptAsync(int caseId, CancellationToken ct)
    {
        var header = Request.Headers["X-Upload-Key"];
        if (header.Count == 0) return null;
        if (header.Count != 1 || !Guid.TryParse(header[0], out var key) || key == Guid.Empty)
            return BadRequest(new { message = "Некорректный идентификатор финансовой попытки." });
        var actorId = User.GetUserId();
        if (actorId is not > 0) return Unauthorized();
        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null) return MissingCasesVisibilityResult();
        if (!await _db.ClientCases.AsNoTracking().ApplyClientCaseVisibility(visibility).AnyAsync(c => c.Id == caseId, ct))
            return NotFound();
        var blocked = await GetLegacyCaseUploadBlockAsync(caseId, actorId.Value, key, ct);
        return blocked is null ? null : LegacyCaseUploadBlockedResult(blocked).Result;
    }

    private async Task<ActionResult> GenerateManagedFinancialDocumentAsync(int caseId, GeneratedFinancialDocumentKind kind,
        CreateClientCaseWorkOrderInvoiceRequest? invoiceRequest, CreateClientCaseWorkOrderActRequest? actRequest,
        CreateClientCaseWorkOrderContractRequest? contractRequest, CancellationToken ct)
    {
        var header = Request.Headers["X-Upload-Key"];
        if (header.Count != 1 || !Guid.TryParse(header[0], out var key) || key == Guid.Empty)
            return BadRequest(new { message = "Для формирования финансового документа требуется корректный идентификатор попытки." });
        var actorId = User.GetUserId();
        if (actorId is not > 0) return Unauthorized();
        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null) return MissingCasesVisibilityResult();
        if (!await _db.ClientCases.AsNoTracking().ApplyClientCaseVisibility(visibility).AnyAsync(c => c.Id == caseId, ct))
            return NotFound();
        var orderId = await _db.ClientCaseWorkOrders.AsNoTracking().Where(o => o.ClientCaseId == caseId)
            .Select(o => (int?)o.Id).SingleOrDefaultAsync(ct);
        if (!orderId.HasValue) return NotFound(new { message = "По делу ещё нет заказ-наряда." });
        if (_financialDocumentPublication is null) return FinancialPendingResult(key);
        var requestHash = FinancialRequestHash(caseId, kind, invoiceRequest, actRequest, contractRequest);
        var registry = new GeneratedFinancialDocumentRegistry(_db);
        GeneratedFinancialCaptureResult capture;
        try
        {
            capture = await registry.CaptureAsync(caseId, orderId.Value, actorId.Value, User.GetDisplayName(), key,
                kind, requestHash, (clientCase, order, identity, token) => BuildFinancialCaptureAsync(clientCase, order,
                    identity, kind, invoiceRequest, actRequest, contractRequest, token), ct);
        }
        catch (FinancialCaptureRejectedException)
        {
            return BadRequest(new { message = "Проверьте услуги заказ-наряда и клиента перед созданием финансового документа." });
        }
        if (capture.Status != "recorded" || capture.Receipt is null) return FinancialFailureResult(capture.Status, key);
        var receipt = capture.Receipt;
        var publication = _financialDocumentPublication;
        ManagedCaseDocumentPublication atomic = (path, publish, token) => SaveCaseDocumentAtomicallyAsync(path,
            () => Task.CompletedTask, publish, token, preserveFileOnFailure: true);
        var managed = _managedCaseUploads!;
        var result = await managed.TryResumeGeneratedAsync(caseId, actorId.Value, key, "financial", requestHash,
            (_, _, _) => Task.FromResult(true), publication.PersistAsync, atomic, ct, publication.CanPublishAsync);
        if (result is null || result.Status == "unavailable" && !await registry.HasFileCommitAsync(receipt, ct))
        {
            var frozen = await registry.ReadFrozenAsync(receipt, ct);
            if (frozen is null) return Forbid();
            using var stream = new MemoryStream(frozen.Bytes, writable: false);
            var fileName = GeneratedFinancialDocumentRegistry.FileName(receipt.Proof);
            var file = new FormFile(stream, 0, frozen.Bytes.Length, "file", fileName)
            { Headers = new HeaderDictionary(), ContentType = "text/html; charset=utf-8" };
            result = await managed.GenerateAsync(new(caseId, actorId.Value, frozen.Domain.ActorName, key, file,
                fileName, GeneratedFinancialDocumentRegistry.DocumentType(receipt.Proof.Kind)!, "Черновик",
                GeneratedFinancialDocumentRegistry.DocumentKey(receipt.Proof)), receipt.Generation,
                (_, _, _) => Task.FromResult(true), publication.PersistAsync, atomic, ct, publication.CanPublishAsync);
        }
        if (result.Status != "uploaded" || !result.DocumentId.HasValue) return FinancialFailureResult(result.Status, key);
        var domain = await registry.ReadDomainAsync(receipt.Generation, ct);
        if (domain is null) return Forbid();
        var currentOrder = await _db.ClientCaseWorkOrders.AsNoTracking().Include(o => o.Items)
            .SingleOrDefaultAsync(o => o.Id == orderId && o.ClientCaseId == caseId, ct);
        if (currentOrder is null) return NotFound();
        var dto = MapWorkOrder(currentOrder);
        await CreateCaseDocumentNotifications().FlushCommittedAsync(ct);
        await _realtimePublisher.PublishTaskJournalChangedAsync(ct);
        Response.Headers["X-Upload-Key"] = key.ToString("D");
        return kind switch
        {
            GeneratedFinancialDocumentKind.Invoice => Created($"/invoices/{domain.Identity.EntityId}", new ClientCaseWorkOrderInvoiceDto
            {
                PaymentId = domain.Identity.EntityId,
                InvoiceEntityId = domain.Identity.InvoiceEntityId,
                Number = domain.Number,
                Date = domain.Date,
                DueDate = domain.DueDate!.Value,
                Amount = domain.Amount,
                DocumentId = result.DocumentId,
                WorkOrder = dto
            }),
            GeneratedFinancialDocumentKind.Act => Created($"/acts/{domain.Identity.EntityId}", new ClientCaseWorkOrderActDto
            {
                ActId = domain.Identity.EntityId,
                Number = domain.Number,
                Date = domain.Date,
                Amount = domain.Amount,
                DocumentId = result.DocumentId,
                LineItemCount = domain.Lines.Length,
                WorkOrder = dto
            }),
            _ => Created($"/contracts/{domain.Identity.EntityId}", new ClientCaseWorkOrderContractDto
            {
                ContractId = domain.Identity.EntityId,
                Number = domain.Number,
                Date = domain.Date,
                StartDate = domain.StartDate!.Value,
                Amount = domain.Amount,
                DocumentId = result.DocumentId,
                ClientId = domain.ClientId,
                LegalEntityId = domain.LegalEntityId,
                WorkOrder = dto
            })
        };
    }

    private ActionResult FinancialFailureResult(string status, Guid key) => status switch
    {
        "forbidden" => Forbid(),
        "notFound" => NotFound(),
        "invalid" => BadRequest(new { message = "Проверьте данные и размер финансового документа." }),
        "conflict" => Conflict(new { message = "Данные этой попытки изменились или услуги уже заняты. Проверьте состояние документа перед новой попыткой." }),
        "retired" => Conflict(new { message = "Эта попытка прекращена. Начните отдельное новое формирование." }),
        _ => FinancialPendingResult(key)
    };

    private ActionResult FinancialPendingResult(Guid key)
    {
        Response.Headers["X-Upload-Key"] = key.ToString("D");
        Response.Headers["Retry-After"] = "5";
        return StatusCode(503, new { status = "pending", message = "Обязательные копии ещё не готовы или хранилище недоступно. Финансовая запись и документ появятся вместе после копирования. Повторите ту же попытку позже." });
    }

    private static DateTime? FinancialRequestDate(DateTime? value)
        => value.HasValue ? DateTime.SpecifyKind(value.Value.Date, DateTimeKind.Unspecified) : null;

    private static string FinancialRequestHash(int caseId, GeneratedFinancialDocumentKind kind,
        CreateClientCaseWorkOrderInvoiceRequest? invoice, CreateClientCaseWorkOrderActRequest? act,
        CreateClientCaseWorkOrderContractRequest? contract)
    {
        object request = kind switch
        {
            GeneratedFinancialDocumentKind.Invoice => new
            {
                number = NormalizeOptional(invoice?.Number),
                date = FinancialRequestDate(invoice?.Date),
                dueDate = FinancialRequestDate(invoice?.DueDate),
                description = NormalizeOptional(invoice?.Description)
            },
            GeneratedFinancialDocumentKind.Act => new
            {
                number = NormalizeOptional(act?.Number),
                date = FinancialRequestDate(act?.Date),
                title = NormalizeOptional(act?.Title),
                comment = NormalizeOptional(act?.Comment)
            },
            _ => (object)new
            {
                number = NormalizeOptional(contract?.Number),
                date = FinancialRequestDate(contract?.Date),
                startDate = FinancialRequestDate(contract?.StartDate),
                validUntil = FinancialRequestDate(contract?.ValidUntil),
                isOpenEnded = contract?.IsOpenEnded,
                title = NormalizeOptional(contract?.Title),
                description = NormalizeOptional(contract?.Description)
            }
        };
        var json = JsonSerializer.Serialize(new { version = 1, caseId, kind = kind.ToString(), request }, ManagedGeneratedDocumentJsonOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }
}
