using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Storage;

// The bytes are frozen separately by the managed writer. This server snapshot
// identifies their source and the original wording; it is not a financial graph.
public sealed record GeneratedWorkOrderDocumentSnapshot(int Version, int CaseId, int WorkOrderId,
    string Title, string DocumentStatus);

public sealed class GeneratedWorkOrderDocumentPublication(PaymentContext db, CaseDocumentPublicationRecords records)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static GeneratedWorkOrderDocumentSnapshot? ReadSnapshot(ManagedCaseDocumentGeneration? generation)
    {
        if (generation?.Kind != "work-order" || generation.FrozenDomainJson is not { Length: > 0 and <= 48000 }) return null;
        try
        {
            var value = JsonSerializer.Deserialize<GeneratedWorkOrderDocumentSnapshot>(generation.FrozenDomainJson, JsonOptions);
            return value is { Version: 1, CaseId: > 0, WorkOrderId: > 0, Title.Length: <= 200 }
                && value.DocumentStatus is "Согласован" or "Подготовлен" ? value : null;
        }
        catch (JsonException) { return null; }
    }

    internal static bool SupportsIntent(ManagedCaseDocumentUploadService.UploadIntent? intent)
        => ManagedCaseDocumentUploadService.IsValidResumeIntent(intent)
            && intent!.Generation is not null && SupportsSource(ManagedCaseDocumentUploadService.GenerationSource(intent));

    private static bool SupportsSource(ManagedCaseDocumentGenerationSource intent)
        => ReadSnapshot(intent.Generation) is { } source && source.CaseId == intent.CaseId
            && intent.DocumentKey == $"work-order-{source.WorkOrderId}" && intent.RequestedDocumentKey == intent.DocumentKey
            && intent.FileName == $"case-{source.CaseId}-work-order.html" && intent.DocumentType == "Заказ-наряд"
            && intent.Status == source.DocumentStatus && intent.ContentType == "text/html; charset=utf-8";

    public async Task<bool> CanPublishAsync(ManagedCaseDocumentGenerationSource source, CancellationToken ct)
        => SupportsSource(source) && await CanPublishAsync(source.ActorId, source.CaseId, source.Generation, ct);

    internal static bool CanSchedule(string? json, string owner)
    {
        var intent = ManagedCaseDocumentUploadService.ParseIntent(json);
        return SupportsIntent(intent) && !intent!.DocumentId.HasValue
            && owner == $"generated:case:{intent.CaseId}:{intent.ActorId}:{intent.UploadKey:N}";
    }

    public async Task<bool> CanPublishAsync(int actorId, int caseId, ManagedCaseDocumentGeneration generation, CancellationToken ct)
        => ReadSnapshot(generation) is { } source && source.CaseId == caseId
            && await CaseDocumentUploadAuthorization.CanPublishAsync(db, actorId, caseId, ct)
            && await db.ClientCaseWorkOrders.AsNoTracking().AnyAsync(o => o.Id == source.WorkOrderId && o.ClientCaseId == caseId, ct);

    public async Task PersistAsync(ClientCaseDocument document, ManagedCaseDocumentGeneration generation, CancellationToken ct)
    {
        var source = ReadSnapshot(generation);
        if (source is null || document.ClientCaseId != source.CaseId
            || document.DocumentKey != $"work-order-{source.WorkOrderId}"
            || document.OriginalFileName != $"case-{source.CaseId}-work-order.html"
            || document.DocumentType != "Заказ-наряд" || document.Status != source.DocumentStatus
            || !await CanPublishAsync(document.UploadedByUserId ?? 0, source.CaseId, generation, ct))
            throw new InvalidOperationException("Generated work order publication requires its exact source and authority.");
        var clientCase = await db.ClientCases.SingleAsync(c => c.Id == source.CaseId, ct);
        await records.PersistGeneratedWorkOrderAsync(clientCase, document, source, ct);
    }
}
