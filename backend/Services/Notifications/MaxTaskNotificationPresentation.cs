using System.Globalization;
using System.Text;
using System.Text.Json;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Notifications;

internal static class MaxTaskNotificationPresentation
{
    internal const int MaximumMetadataCharacters = 16000;
    internal sealed record TaskData(int Id, string Title, DateTime DueAt, string Status, string Priority, string AssigneeName);

    internal static Uri? FrontendUri(IConfiguration? configuration)
    {
        var value = (configuration?["App:FrontendUrl"] ?? configuration?["Frontend:BaseUrl"]
            ?? configuration?["Frontend:PublicUrl"])?.Trim();
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) ? uri : null;
    }

    internal static Uri TaskUri(Uri frontend, int taskId) => new(frontend, $"/tasks/{taskId.ToString(CultureInfo.InvariantCulture)}");

    internal static bool TryTaskId(Uri? uri, Uri? frontend, out int taskId)
    {
        taskId = 0;
        return uri is { IsAbsoluteUri: true } && frontend is not null
            && uri.Scheme == frontend.Scheme && uri.IdnHost == frontend.IdnHost && uri.Port == frontend.Port
            && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
            && uri.AbsolutePath.StartsWith("/tasks/", StringComparison.Ordinal)
            && int.TryParse(uri.AbsolutePath[7..], NumberStyles.None, CultureInfo.InvariantCulture, out taskId)
            && taskId > 0 && uri.AbsoluteUri == TaskUri(frontend, taskId).AbsoluteUri;
    }

    internal static string Render(TaskData task, string notificationType, Uri uri, TimeZoneInfo timeZone,
        string? metadataJson = null, string? actionText = "Открыть задачу")
    {
        var definition = UserNotificationTypes.Find(notificationType)
            ?? throw new ArgumentException("Неизвестный тип уведомления задачи.", nameof(notificationType));
        var label = NormalizeLine(definition.Label, 80, "Обновление задачи");
        var dueAt = CalendarTimeZoneHelper.ToUserLocal(task.DueAt, timeZone).ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
        var status = task.Status.Trim().ToLowerInvariant() switch
        {
            "new" => "Новая",
            "inprogress" or "in_progress" => "В работе",
            "waiting" => "Ожидание",
            "onapproval" or "awaiting_approval" => "На согласовании",
            "approved" => "Согласована",
            "revision" => "На доработке",
            "overdueapproval" => "Согласование просрочено",
            "done" => "Завершена",
            "cancelled" => "Отменена",
            _ => "Не указан",
        };
        var taskPriority = task.Priority.Trim().ToLowerInvariant() switch
        {
            "low" => "Низкий",
            "normal" => "Обычный",
            "high" => "Высокий",
            "urgent" => "Срочно",
            _ => "Не указан",
        };
        var (summary, action) = notificationType switch
        {
            UserNotificationTypes.TaskAssigned => ("Задача назначена исполнителю.", "Ознакомиться с задачей и сроком выполнения"),
            UserNotificationTypes.TaskReminder => ("Напоминание о выполнении задачи.", "Проверить выполнение задачи и действующий срок"),
            UserNotificationTypes.TaskChanged => ("Изменены данные задачи.", "Проверить изменения в карточке задачи"),
            UserNotificationTypes.TaskComment => ("В задаче появился новый комментарий.", "Прочитать комментарий в карточке задачи"),
            UserNotificationTypes.TaskCompleted => ("Работа по задаче завершена.", "Ознакомиться с результатом в карточке задачи"),
            UserNotificationTypes.TaskCancelled => ("Задача отменена.", "не требуется"),
            UserNotificationTypes.TaskScheduleConflict => ("Обнаружено пересечение задач в расписании.", "Проверить расписание и сроки задач"),
            UserNotificationTypes.TaskApprovalRequested => ("Задача передана на согласование.", "Принять решение в карточке задачи"),
            UserNotificationTypes.TaskApproved => ("Задача согласована.", "не требуется"),
            UserNotificationTypes.TaskRevisionRequested => ("Запрошена доработка задачи.", "Ознакомиться с замечаниями и доработать задачу"),
            UserNotificationTypes.TaskDeadlineChangeRequested => ("Запрошен перенос срока задачи.", "Рассмотреть запрос в карточке задачи"),
            UserNotificationTypes.TaskDeadlineChangeApproved => ("Перенос срока задачи согласован.", "Ознакомиться с новым сроком задачи"),
            UserNotificationTypes.TaskDeadlineChangeRejected => ("Запрос переноса срока задачи отклонён.", "Проверить действующий срок и решение в карточке задачи"),
            UserNotificationTypes.TaskApprovalOverdue => ("Срок согласования задачи истёк.", "Рассмотреть задачу в карточке согласования"),
            _ => (label, "Ознакомиться с обновлением в карточке задачи"),
        };
        if (notificationType == UserNotificationTypes.TaskChanged && ReadChanges(metadataJson) is { } changes)
            summary = changes;
        return Compose(definition,
            $"задача №{task.Id.ToString(CultureInfo.InvariantCulture)}, {NormalizeLine(task.Title, 160, "Без названия")}",
            summary,
            $"срок {dueAt} ({NormalizeLine(timeZone.Id, 64, "UTC")}), статус {status}, приоритет {taskPriority}, исполнитель {NormalizeLine(task.AssigneeName, 80, "не назначен")}",
            action, uri, actionText);
    }

    internal static string RenderNeutral(string notificationType, Uri? uri, string? actionText)
    {
        var definition = UserNotificationTypes.Find(notificationType)
            ?? throw new ArgumentException("Неизвестный тип уведомления задачи.", nameof(notificationType));
        var action = notificationType is UserNotificationTypes.TaskCancelled or UserNotificationTypes.TaskApproved
            ? "не требуется" : "Ознакомиться с уведомлением в CRM";
        return Compose(definition, "задача в CRM", definition.Label, "Срок и исполнитель не указаны", action, uri, actionText);
    }

    private static string Compose(UserNotificationTypeDefinition definition, string taskObject, string summary,
        string data, string action, Uri? uri, string? actionText)
    {
        var label = NormalizeLine(definition.Label, 80, "Обновление задачи");
        var prioritySuffix = definition.Priority switch
        {
            NotificationPriorities.Critical => " — СРОЧНО",
            NotificationPriorities.High => " — ВЫСОКИЙ",
            _ => string.Empty,
        };
        var button = uri is null ? "отсутствуют" : NormalizeLine(actionText, 80, "отсутствуют");
        var lines = new[]
        {
            $"[{label.ToUpperInvariant()}] {label}{prioritySuffix}",
            $"Объект: {taskObject}",
            $"Суть: {summary}",
            $"Данные: {data}",
            $"Действие: {action}",
            "Контекст: ",
            $"Кнопки: {button}",
        };
        var context = uri?.AbsoluteUri ?? "уведомления CRM";
        var contextBudget = MaxApiClient.MaxTextLength - lines.Sum(line => line.Length) - (lines.Length - 1);
        if (context.Length > contextBudget)
            context = uri is not null && !string.IsNullOrWhiteSpace(actionText)
                ? $"полная ссылка доступна по кнопке «{button}»"
                : "откройте уведомления в CRM";
        lines[5] += context;
        return string.Join('\n', lines);
    }

    private static string? ReadChanges(string? metadataJson)
    {
        if (metadataJson is not { Length: > 0 and <= MaximumMetadataCharacters }) return null;
        try
        {
            using var json = JsonDocument.Parse(metadataJson);
            if (json.RootElement.ValueKind != JsonValueKind.Object
                || !json.RootElement.TryGetProperty("changeLabels", out var labels)
                || labels.ValueKind != JsonValueKind.Array)
                return null;
            var changes = labels.EnumerateArray().Take(32)
                .Where(label => label.ValueKind == JsonValueKind.String)
                .Select(label => ChangedField(label.GetString()!))
                .Where(field => field is not null).Distinct(StringComparer.Ordinal).ToArray();
            return changes.Length == 0 ? null : "Изменены поля: " + string.Join(", ", changes);
        }
        catch (JsonException) { return null; }
    }

    private static string? ChangedField(string label)
    {
        var separator = label.IndexOf(':');
        var field = separator < 0 ? label : label[..separator];
        return field.Trim().ToLowerInvariant() switch
        {
            "срок" => "срок",
            "статус" => "статус",
            "приоритет" => "приоритет",
            "исполнитель" => "исполнитель",
            _ => null,
        };
    }

    private static string NormalizeLine(string? value, int maximumElements, string fallback)
    {
        var clean = new StringBuilder();
        var pendingSpace = false;
        foreach (var rune in (value ?? string.Empty).EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (Rune.IsWhiteSpace(rune) || category == UnicodeCategory.Control)
            {
                pendingSpace = clean.Length > 0;
                continue;
            }
            // Убираем управляющие направлением письма знаки, сохраняя ZWJ в emoji.
            if (category == UnicodeCategory.Format && rune.Value != 0x200D) continue;
            if (pendingSpace) clean.Append(' ');
            pendingSpace = false;
            clean.Append(rune.ToString());
        }
        var normalized = clean.ToString();
        var boundaries = StringInfo.ParseCombiningCharacters(normalized);
        var elements = Math.Min(maximumElements, boundaries.Length);
        var end = elements == boundaries.Length ? normalized.Length : boundaries[elements];
        while (elements > 0 && end > maximumElements * 4)
        {
            elements--;
            end = elements == 0 ? 0 : boundaries[elements];
        }
        if (end == 0) return fallback;
        if (end < normalized.Length) normalized = normalized[..end] + "…";
        return normalized.Length == 0 ? fallback : normalized;
    }
}
