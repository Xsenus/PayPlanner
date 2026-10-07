using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;

namespace PayPlanner.Api.Services;

public sealed class CaseDocumentPublicationRecords(PaymentContext db, CaseDocumentNotificationService notifications)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task PersistGeneratedFinancialAsync(ClientCase clientCase, ClientCaseDocument document,
        GeneratedFinancialDomainSnapshot source, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null || source.Version != 1 || clientCase.Id != source.CaseId
            || document.ClientCaseId != source.CaseId || document.UploadedByUserId != source.ActorId
            || document.UploadedByName != source.ActorName || document.Id <= 0)
            throw new InvalidOperationException("Financial document records require their accepted publication transaction.");
        var label = source.Kind switch
        {
            GeneratedFinancialDocumentKind.Invoice => "Счет",
            GeneratedFinancialDocumentKind.Act => "Акт",
            GeneratedFinancialDocumentKind.Contract => "Договор",
            _ => throw new InvalidOperationException("Unsupported financial kind.")
        };
        AppendEvent(clientCase,
            $"{label} №{source.Number} из заказ-наряда «{source.WorkOrderTitle}» сохранен в документы дела. Автор: {source.ActorName}.",
            DateTime.SpecifyKind(document.UploadedAt, DateTimeKind.Utc).ToLocalTime());
        AddAuditEvent(db, clientCase, document, "Generated",
            $"Сформирован документ {label.ToLowerInvariant()} №{source.Number} из заказ-наряда «{source.WorkOrderTitle}».", source.ActorId, source.ActorName);
        await notifications.NotifyAsync(clientCase, document, "generated", source.ActorId, source.ActorName, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task PersistGeneratedCourtAsync(ClientCase clientCase, ClientCaseDocument document,
        GeneratedCourtDocumentSnapshot source, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null || source.Version != 2 || source.CaseId != clientCase.Id
            || document.ClientCaseId != source.CaseId || document.UploadedByUserId != source.ActorId
            || document.DocumentKey != source.DocumentKey)
            throw new InvalidOperationException("Court document records require their matching publication transaction.");
        await db.Entry(clientCase).ReloadAsync(ct);
        var text = $"Сводка судебного события «{source.Title}» добавлена из ГАС «Правосудие»; текст акта не загружен.";
        AppendEvent(clientCase, $"{text} Автор: {source.ActorName}.",
            DateTime.SpecifyKind(document.UploadedAt, DateTimeKind.Utc).ToLocalTime());
        await db.SaveChangesAsync(ct);
        // Canonical source keys were assigned by the accepted control pass before file I/O.
        // Do not guess legacy/null identities or create deadlines and reminders a second time.
        var deadlines = await db.ClientCaseProceduralDeadlines.Where(d => d.ClientCaseId == source.CaseId
            && d.SourceType == "sudrf" && d.SourceDocumentKey == source.DocumentKey && d.Status != "archived").ToListAsync(ct);
        foreach (var deadline in deadlines) deadline.ClientCaseDocumentId = document.Id;
        AddAuditEvent(db, clientCase, document, "Created", text, source.ActorId, source.ActorName);
        await notifications.NotifyAsync(clientCase, document, "uploaded", source.ActorId, source.ActorName, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task PersistGeneratedTemplateAsync(ClientCase clientCase, ClientCaseDocument document,
        GeneratedTemplateDocumentSnapshot source, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null || source.Version != 1 || source.CaseId != clientCase.Id
            || document.ClientCaseId != clientCase.Id || document.UploadedByUserId is not > 0)
            throw new InvalidOperationException("Generated template records require their matching publication transaction.");
        await db.Entry(clientCase).ReloadAsync(ct);
        AppendEvent(clientCase,
            $"Сформирован шаблон «{document.OriginalFileName}»: тип «{document.DocumentType}», статус «{document.Status}», версия {document.Version}. Автор: {document.UploadedByName}.",
            DateTime.SpecifyKind(document.UploadedAt, DateTimeKind.Utc).ToLocalTime());
        await db.SaveChangesAsync(ct);
        AddAuditEvent(db, clientCase, document, "Generated",
            $"Сформирован документ из шаблона «{document.OriginalFileName}».", document.UploadedByUserId, document.UploadedByName);
        await notifications.NotifyAsync(clientCase, document, "generated", document.UploadedByUserId, document.UploadedByName, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task PersistGeneratedWorkOrderAsync(ClientCase clientCase, ClientCaseDocument document,
        GeneratedWorkOrderDocumentSnapshot source, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null || source.Version != 1 || source.CaseId != clientCase.Id
            || document.ClientCaseId != clientCase.Id || document.UploadedByUserId is not > 0)
            throw new InvalidOperationException("Generated work order records require their matching publication transaction.");
        // This standalone print operation owns no tracked court/financial graph.
        // Preserve history appended by another request before the case lock.
        await db.Entry(clientCase).ReloadAsync(ct);
        AppendEvent(clientCase,
            $"Заказ-наряд «{source.Title}» сохранен в документы дела. Автор: {document.UploadedByName}.",
            DateTime.SpecifyKind(document.UploadedAt, DateTimeKind.Utc).ToLocalTime());
        await db.SaveChangesAsync(ct);
        AddAuditEvent(db, clientCase, document, "Generated",
            $"Сформирована печатная форма заказ-наряда «{source.Title}».", document.UploadedByUserId, document.UploadedByName);
        await notifications.NotifyAsync(clientCase, document, "generated", document.UploadedByUserId, document.UploadedByName, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task PersistUploadAsync(ClientCase clientCase, ClientCaseDocument document, bool isFirstVersion,
        int actorUserId, string actorName, DateTime localNow, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Case upload records require their publication transaction.");
        if (clientCase.Id <= 0 || document.ClientCaseId != clientCase.Id || actorUserId <= 0)
            throw new InvalidOperationException("Case upload records require a matching case and actor.");
        // The caller owns the publication lock. Reload before appending to current history.
        await db.Entry(clientCase).ReloadAsync(ct);
        var prefix = isFirstVersion ? $"Документ «{document.OriginalFileName}» загружен"
            : $"Документ «{document.OriginalFileName}»: загружена новая версия";
        AppendEvent(clientCase,
            $"{prefix}: тип «{document.DocumentType}», статус «{document.Status}», версия {document.Version}. Добавил: {actorName}.", localNow);
        await db.SaveChangesAsync(ct);
        AddAuditEvent(db, clientCase, document, isFirstVersion ? "Created" : "VersionCreated",
            isFirstVersion ? $"Документ «{document.OriginalFileName}» загружен."
                : $"Загружена новая версия документа «{document.OriginalFileName}».", actorUserId, actorName);
        await notifications.NotifyAsync(clientCase, document, isFirstVersion ? "uploaded" : "version-created",
            actorUserId, actorName, ct);
        await db.SaveChangesAsync(ct);
    }

    public static void AddAuditEvent(
        PaymentContext db, ClientCase clientCase,
        ClientCaseDocument document,
        string action,
        string description, int? actorUserId, string actorName)
    {
        db.ObjectAuditEvents.Add(new ObjectAuditEvent
        {
            ObjectType = "client-case-document",
            ObjectId = document.Id,
            Action = action,
            Description = description,
            ActorUserId = actorUserId,
            ActorUserName = actorName,
            MetadataJson = JsonSerializer.Serialize(new
            {
                clientCaseId = clientCase.Id,
                caseTitle = clientCase.Title,
                documentKey = document.DocumentKey,
                version = document.Version,
                documentType = document.DocumentType,
                status = document.Status,
                fileName = document.OriginalFileName,
            }, JsonOptions),
            CreatedAt = DateTime.UtcNow,
        });
    }

    public static void AppendEvent(ClientCase clientCase, string eventText, DateTime localNow)
    {
        var line = $"{localNow.ToString("dd.MM.yyyy HH:mm", CultureInfo.GetCultureInfo("ru-RU"))}: {eventText}";
        var existing = clientCase.EventHistory?.Trim();
        var next = string.IsNullOrWhiteSpace(existing)
            ? line
            : $"{existing}{Environment.NewLine}{line}";

        clientCase.EventHistory = next.Length <= 4000
            ? next
            : next[^4000..].TrimStart();
    }
}
