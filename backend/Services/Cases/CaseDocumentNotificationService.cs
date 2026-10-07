using System.Globalization;
using System.Text;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;

namespace PayPlanner.Api.Services;

public sealed class CaseDocumentNotificationService(PaymentContext db,
    INotificationService? notifications = null, Func<INotificationService>? fallbackFactory = null)
{
    public async Task FlushCommittedAsync(CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null) return;
        var ids = db.ChangeTracker.Entries<CaseDocumentNotificationDispatch>()
            .Where(e => e.Entity.Status == "pending" && e.Entity.Channel == "realtime")
            .Select(e => e.Entity.Id).Take(100).ToArray();
        if (ids.Length == 0) return;
        var service = notifications ?? fallbackFactory?.Invoke();
        if (service is not ICaseDocumentNotificationLiveDispatcher live) return;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        var processor = new CaseDocumentNotificationDispatchProcessor(db, live);
        foreach (var id in ids)
        {
            try { await processor.ProcessAsync(id, budget.Token); }
            catch (Exception) { break; } // Durable rows remain available to the worker; publication already committed.
        }
    }

    private static string BuildCaseNotificationTitle(ClientCase clientCase)
    {
        if (!string.IsNullOrWhiteSpace(clientCase.CaseNumber))
        {
            return $"№{clientCase.CaseNumber} · {clientCase.Title}";
        }

        return string.IsNullOrWhiteSpace(clientCase.Title)
            ? $"Дело #{clientCase.Id}"
            : clientCase.Title;
    }

    public async Task NotifyAsync(
        ClientCase clientCase,
        ClientCaseDocument document,
        string action,
        int? actorUserId,
        string actorName,
        CancellationToken ct)
    {
        var recipientUserIds = await CaseNotificationRecipients.ResolveAsync(db, clientCase.Id, ct);
        if (recipientUserIds.Count == 0)
        {
            return;
        }

        if (notifications is not null)
        {
            await notifications.NotifyCaseDocumentAsync(
                clientCase,
                document,
                action,
                actorName,
                actorUserId ?? 0,
                recipientUserIds,
                ct);
            return;
        }

        var title = action switch
        {
            "uploaded" => $"Загружен документ: {BuildCaseNotificationTitle(clientCase)}",
            "version-created" => $"Новая версия документа: {BuildCaseNotificationTitle(clientCase)}",
            "generated" => $"Сформирован документ: {BuildCaseNotificationTitle(clientCase)}",
            "updated" => $"Обновлен документ: {BuildCaseNotificationTitle(clientCase)}",
            "deleted" => $"Удален документ: {BuildCaseNotificationTitle(clientCase)}",
            _ => $"Документ дела: {BuildCaseNotificationTitle(clientCase)}",
        };
        var body = BuildCaseDocumentNotificationBody(clientCase, document, action, actorName);
        var eventKey = $"case:{clientCase.Id}:document:{document.Id}:{action}:{DateTime.UtcNow.Ticks}";
        var metadata = new
        {
            clientCaseId = clientCase.Id,
            clientCaseDocumentId = document.Id,
            action,
            actorUserId,
            documentKey = document.DocumentKey,
            version = document.Version,
            documentType = document.DocumentType,
            status = document.Status,
            fileName = document.OriginalFileName,
        };

        foreach (var recipientUserId in recipientUserIds.OrderBy(static id => id))
        {
            await AddCaseUserNotificationAsync(
                recipientUserId,
                UserNotificationTypes.CaseDocument,
                eventKey,
                title,
                body,
                "emerald",
                clientCase,
                metadata,
                ct);
        }
    }

    private static string BuildCaseDocumentNotificationBody(
        ClientCase clientCase,
        ClientCaseDocument document,
        string action,
        string actorName)
    {
        var actionText = action switch
        {
            "uploaded" => "В дело загружен документ.",
            "version-created" => "В дело загружена новая версия документа.",
            "generated" => "В деле сформирован документ.",
            "updated" => "В деле обновлена карточка документа.",
            "deleted" => "Из дела удален документ.",
            _ => "В деле изменен документ.",
        };

        return new StringBuilder()
            .Append(actionText)
            .AppendLine()
            .Append("Дело: ")
            .Append(BuildCaseNotificationTitle(clientCase))
            .AppendLine()
            .Append("Документ: ")
            .Append(document.OriginalFileName)
            .AppendLine()
            .Append("Тип: ")
            .Append(document.DocumentType)
            .AppendLine()
            .Append("Статус: ")
            .Append(document.Status)
            .AppendLine()
            .Append("Версия: ")
            .Append(document.Version.ToString(CultureInfo.InvariantCulture))
            .AppendLine()
            .Append("Автор: ")
            .Append(actorName)
            .ToString();
    }

    private async Task AddCaseUserNotificationAsync(int recipientUserId, string type, string eventKey,
        string title, string body, string tone, ClientCase clientCase, object? metadata, CancellationToken ct)
    {
        var service = fallbackFactory?.Invoke()
            ?? throw new InvalidOperationException("Case document notifications require a notification service.");
        await service.NotifyAsync(new NotificationEventEnvelope(recipientUserId, type, eventKey, title, body,
            "case", clientCase.Id, null, tone, metadata), ct);
    }
}
