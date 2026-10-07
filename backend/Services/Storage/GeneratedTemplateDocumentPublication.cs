using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Documents;

namespace PayPlanner.Api.Services.Storage;

public sealed record GeneratedTemplateDocumentSnapshot(int Version, int CaseId, string TemplateKey,
    int TemplateVersion, string? RequestedTemplateKey, string? RequestedDocumentType,
    string FileName, string DocumentType, string DocumentStatus);

public sealed class GeneratedTemplateDocumentPublication(PaymentContext db, CaseDocumentPublicationRecords records)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static GeneratedTemplateDocumentSnapshot? ReadSnapshot(ManagedCaseDocumentGeneration? generation)
    {
        if (generation?.Kind != "template" || generation.FrozenDomainJson is not { Length: > 0 and <= 48000 }) return null;
        try
        {
            var source = JsonSerializer.Deserialize<GeneratedTemplateDocumentSnapshot>(generation.FrozenDomainJson, JsonOptions);
            return source is
            {
                Version: 1, CaseId: > 0, TemplateVersion: > 0, FileName.Length: > 0 and <= 260,
                DocumentType.Length: > 0 and <= 120, DocumentStatus.Length: > 0 and <= 120
            }
                && source.TemplateKey == CaseDocumentTemplateCatalog.NormalizeDocumentTemplateCode(source.TemplateKey)
                && source.TemplateKey is { Length: > 0 }
                && source.RequestedTemplateKey is not { Length: > 64 }
                && source.RequestedDocumentType is not { Length: > 120 }
                && !source.FileName.Contains('/') && !source.FileName.Contains('\\') ? source : null;
        }
        catch (JsonException) { return null; }
    }

    internal static bool SupportsIntent(ManagedCaseDocumentUploadService.UploadIntent? intent)
        => ManagedCaseDocumentUploadService.IsValidResumeIntent(intent)
            && intent!.Generation is not null && SupportsSource(ManagedCaseDocumentUploadService.GenerationSource(intent));

    private static bool SupportsSource(ManagedCaseDocumentGenerationSource intent)
        => ReadSnapshot(intent.Generation) is { } source && source.CaseId == intent.CaseId
            && intent.DocumentKey == $"template-{source.TemplateKey}" && intent.RequestedDocumentKey == intent.DocumentKey
            && intent.FileName == source.FileName && intent.DocumentType == source.DocumentType
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
    {
        var source = ReadSnapshot(generation);
        if (source is null || source.CaseId != caseId
            || !await CaseDocumentUploadAuthorization.CanPublishAsync(db, actorId, caseId, ct)) return false;
        var clientCase = await db.ClientCases.AsNoTracking().SingleOrDefaultAsync(c => c.Id == caseId, ct);
        if (clientCase is null) return false;
        var library = await new CaseDocumentTemplateCatalog(db).ReadAsync(ct);
        var selected = CaseDocumentTemplateCatalog.Resolve(library, source.RequestedTemplateKey, source.RequestedDocumentType);
        // Recheck selection and applicability; never rerender updated template content.
        return selected.Error is null && selected.Template.Key == source.TemplateKey
            && CaseDocumentTemplateCatalog.IsCaseDocumentTemplateApplicableToCase(selected.Template, clientCase);
    }

    public async Task PersistAsync(ClientCaseDocument document, ManagedCaseDocumentGeneration generation, CancellationToken ct)
    {
        var source = ReadSnapshot(generation);
        if (source is null || document.ClientCaseId != source.CaseId
            || document.DocumentKey != $"template-{source.TemplateKey}"
            || document.OriginalFileName != source.FileName || document.DocumentType != source.DocumentType
            || document.Status != source.DocumentStatus
            || !await CanPublishAsync(document.UploadedByUserId ?? 0, source.CaseId, generation, ct))
            throw new InvalidOperationException("Generated template publication requires its exact source and authority.");
        var clientCase = await db.ClientCases.SingleAsync(c => c.Id == source.CaseId, ct);
        await records.PersistGeneratedTemplateAsync(clientCase, document, source, ct);
    }
}
