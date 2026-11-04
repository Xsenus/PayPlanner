using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Controllers.V2;

public partial class CasesV2Controller
{
    private sealed class CourtPublicationRejectedException(ActionResult result) : Exception
    {
        public ActionResult Result { get; } = result;
    }

    private Task<IActionResult> WithCurrentCourtPublicationAsync(int id, CourtPublicationPlan plan,
        Func<ClientCase, Task<IActionResult>> write, CancellationToken ct, Action? beforeCommit = null)
        => WithCurrentCourtCheckAsync(id, plan.Source, currentCase =>
        {
            if (CourtSyncCaseState.Capture(currentCase) != plan.Fields)
                return Task.FromResult<IActionResult>(Conflict(new { message = "Судебные сведения изменились во время публикации. Обновите дело перед повторной проверкой." }));
            return write(currentCase);
        }, ct, beforeCommit);

    private async Task<ClientCase> RequireCourtPublicationAsync(int id, CourtPublicationPlan plan, CancellationToken ct)
    {
        var result = await WithCurrentCourtPublicationAsync(id, plan, currentCase => Task.FromResult<IActionResult>(Ok(currentCase)), ct);
        return result is OkObjectResult { Value: ClientCase currentCase } ? currentCase
            : throw new CourtPublicationRejectedException((ActionResult)result);
    }

    private sealed class CourtPublicationPlan(ClientCase clientCase)
    {
        public CourtCheckSource Source { get; } = CourtCheckSource.Capture(clientCase);
        public CourtSyncCaseState Fields { get; } = CourtSyncCaseState.Capture(clientCase);
        public Dictionary<int, CourtDocumentChange> Documents { get; } = [];
        public Dictionary<int, CourtDeadlineLink> Deadlines { get; } = [];
    }

    private sealed record CourtDocumentMetadata(
        int Version, string DocumentKey, string Status, string SourceType, string DocumentType,
        string OriginalFileName, string SourceUrl, DateTime? SourceCheckedAt, string SourceConfidence,
        string StoredFileName, string StoragePath, string ContentType, long SizeBytes)
    {
        public static CourtDocumentMetadata Capture(ClientCaseDocument document) => new(
            document.Version, document.DocumentKey, document.Status, document.SourceType, document.DocumentType,
            document.OriginalFileName, document.SourceUrl, document.SourceCheckedAt, document.SourceConfidence,
            document.StoredFileName, document.StoragePath, document.ContentType, document.SizeBytes);

        public void Apply(ClientCaseDocument document)
        {
            document.DocumentKey = DocumentKey;
            document.Status = Status;
            document.SourceType = SourceType;
            document.DocumentType = DocumentType;
            document.OriginalFileName = OriginalFileName;
            document.SourceUrl = SourceUrl;
            document.SourceCheckedAt = SourceCheckedAt;
            document.SourceConfidence = SourceConfidence;
        }
    }

    private sealed record CourtDocumentChange(CourtDocumentMetadata Before, CourtDocumentMetadata After);
    private sealed record CourtDeadlineLink(int? BeforeDocumentId, string? SourceKey, string SourceType, string SourceUrl, int DocumentId);

    private static CourtDocumentMetadata CurrentCourtDocumentMetadata(CourtPublicationPlan? plan, ClientCaseDocument document)
        => plan is not null && plan.Documents.TryGetValue(document.Id, out var change)
            ? change.After : CourtDocumentMetadata.Capture(document);

    private static void StageCourtDocumentMetadata(CourtPublicationPlan? plan, ClientCaseDocument document, CourtDocumentMetadata after)
    {
        if (plan is null)
        {
            after.Apply(document);
            return;
        }
        if (plan.Documents.TryGetValue(document.Id, out var change))
            plan.Documents[document.Id] = change with { After = after };
        else
        {
            var before = CourtDocumentMetadata.Capture(document);
            if (before != after) plan.Documents.Add(document.Id, new(before, after));
        }
    }

    private async Task<ActionResult?> ApplyCourtPublicationChangesAsync(int caseId, CourtPublicationPlan plan, CancellationToken ct)
    {
        if (plan.Documents.Count == 0 && plan.Deadlines.Count == 0) return null;
        var documentIds = plan.Documents.Keys.Concat(plan.Deadlines.Values.Select(link => link.DocumentId)).Distinct().ToArray();
        var deadlineIds = plan.Deadlines.Keys.ToArray();
        if (_db.Database.IsNpgsql())
        {
            if (documentIds.Length > 0)
                await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCaseDocuments\" WHERE \"Id\" = ANY ({documentIds}) ORDER BY \"Id\" FOR UPDATE NOWAIT", ct);
            if (deadlineIds.Length > 0)
                await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCaseProceduralDeadlines\" WHERE \"Id\" = ANY ({deadlineIds}) ORDER BY \"Id\" FOR UPDATE NOWAIT", ct);
        }
        var documents = await _db.ClientCaseDocuments.AsNoTracking().Where(document => document.ClientCaseId == caseId && documentIds.Contains(document.Id))
            .ToDictionaryAsync(document => document.Id, ct);
        var deadlines = await _db.ClientCaseProceduralDeadlines.AsNoTracking().Where(deadline => deadline.ClientCaseId == caseId && deadlineIds.Contains(deadline.Id))
            .ToDictionaryAsync(deadline => deadline.Id, ct);
        if (documents.Count != documentIds.Length || deadlines.Count != deadlineIds.Length)
            return Conflict(new { message = "Сводка или связанный срок удалены либо перенесены. Обновите дело перед повторной проверкой." });
        var keys = plan.Documents.Values.Select(change => change.Before.DocumentKey).Distinct().ToArray();
        var versions = await _db.ClientCaseDocuments.AsNoTracking().Where(document => document.ClientCaseId == caseId && keys.Contains(document.DocumentKey))
            .GroupBy(document => document.DocumentKey).Select(group => new { Key = group.Key, Version = group.Max(document => document.Version) })
            .ToDictionaryAsync(row => row.Key, row => row.Version, ct);
        foreach (var (id, change) in plan.Documents)
        {
            if (CourtDocumentMetadata.Capture(documents[id]) != change.Before
                || versions.GetValueOrDefault(change.Before.DocumentKey) != change.Before.Version)
                return Conflict(new { message = "Сводка изменена или получила новую версию. Обновите дело перед повторной проверкой." });
        }
        foreach (var (id, link) in plan.Deadlines)
        {
            var deadline = deadlines[id];
            if (deadline.ClientCaseDocumentId != link.BeforeDocumentId || deadline.SourceDocumentKey != link.SourceKey
                || deadline.SourceType != link.SourceType || deadline.SourceUrl != link.SourceUrl)
                return Conflict(new { message = "Связь процессуального срока изменена. Обновите дело перед повторной проверкой." });
        }
        foreach (var (id, change) in plan.Documents)
        {
            var document = documents[id];
            _db.ClientCaseDocuments.Attach(document);
            change.After.Apply(document);
        }
        foreach (var (id, link) in plan.Deadlines)
        {
            var deadline = deadlines[id];
            _db.ClientCaseProceduralDeadlines.Attach(deadline);
            deadline.ClientCaseDocumentId = link.DocumentId;
        }
        return null;
    }
}
