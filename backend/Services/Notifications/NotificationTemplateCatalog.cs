using System.Text;
using System.Text.RegularExpressions;

namespace PayPlanner.Api.Services.Notifications;

public static class NotificationTemplateKeys
{
    public const string RegistrationPending = "registration.pending";
    public const string RegistrationAdminNewUser = "registration.adminNewUser";
    public const string RegistrationApproved = "registration.approved";
    public const string RegistrationRejected = "registration.rejected";
    public const string PasswordIssued = "password.issued";
    public const string ContractTerminationClient = "contract.terminationClient";
    public const string TaskAssigned = "task.assigned";
    public const string TaskReminder = "task.reminder";
    public const string TaskCompleted = "task.completed";
    public const string TaskCancelled = "task.cancelled";
    public const string TaskScheduleConflict = "task.scheduleConflict";
    public const string TaskApprovalRequested = "task.approvalRequested";
    public const string TaskApproved = "task.approved";
    public const string TaskRevisionRequested = "task.revisionRequested";
    public const string TaskApprovalOverdue = "task.approvalOverdue";
    public const string CalendarAccessChanged = "calendar.accessChanged";
    public const string TaskChanged = "task.changed";
    public const string TaskComment = "task.comment";
    public const string CaseInactivityControl = "case.inactivityControl";
}

public sealed record NotificationTemplateVariableDefinition(string Key, string Label);

public sealed record NotificationTemplateDefinition(
    string Key,
    string Label,
    string Description,
    string DefaultSubject,
    string DefaultBody,
    IReadOnlyList<NotificationTemplateVariableDefinition> Variables);

public static class NotificationTemplateCatalog
{
    private static readonly Regex PlaceholderRegex = new(@"\{\{\s*([A-Za-z0-9_.-]+)\s*\}\}", RegexOptions.Compiled);

    private static readonly IReadOnlyList<NotificationTemplateVariableDefinition> UserVariables =
    [
        new("user.name", "Имя пользователя"),
        new("user.email", "Email пользователя")
    ];

    private static readonly IReadOnlyList<NotificationTemplateVariableDefinition> TaskVariables =
    [
        new("task.title", "Название задачи"),
        new("task.objectName", "Объект задачи"),
        new("task.dueAt", "Срок задачи"),
        new("task.status", "Статус задачи"),
        new("task.priority", "Приоритет задачи"),
        new("task.assigneeName", "Исполнитель"),
        new("task.authorName", "Постановщик"),
        new("task.assignedSubject", "Тема уведомления о назначении задачи"),
        new("task.assignmentDetails", "Детали назначения или переназначения"),
        new("actor.name", "Кто выполнил действие")
    ];

    public static readonly IReadOnlyList<NotificationTemplateDefinition> All =
    [
        new(
            NotificationTemplateKeys.RegistrationPending,
            "Регистрация ожидает подтверждения",
            "Письмо пользователю после отправки заявки на регистрацию.",
            "Регистрация ожидает подтверждения",
            "Здравствуйте, {{user.name}}!\n\nВаша заявка на регистрацию получена и ожидает проверки администратором.",
            UserVariables),
        new(
            NotificationTemplateKeys.RegistrationAdminNewUser,
            "Новая регистрация для администратора",
            "Письмо администраторам о новой заявке пользователя.",
            "Новая регистрация в CRM",
            "В системе зарегистрировался новый пользователь.\n\nИмя: {{user.name}}\nEmail: {{user.email}}",
            UserVariables),
        new(
            NotificationTemplateKeys.RegistrationApproved,
            "Регистрация одобрена",
            "Письмо пользователю после одобрения доступа.",
            "Регистрация одобрена",
            "Здравствуйте, {{user.name}}!\n\nДоступ к PayPlanner одобрен. Теперь можно войти в систему.",
            UserVariables),
        new(
            NotificationTemplateKeys.RegistrationRejected,
            "Регистрация отклонена",
            "Письмо пользователю после отклонения заявки.",
            "Регистрация отклонена",
            "Здравствуйте, {{user.name}}!\n\nЗаявка на регистрацию отклонена.\nПричина: {{registration.reason}}",
            [.. UserVariables, new("registration.reason", "Причина отклонения")]),
        new(
            NotificationTemplateKeys.PasswordIssued,
            "Выдан новый пароль",
            "Письмо пользователю при выдаче нового пароля.",
            "Выдан новый пароль",
            "Здравствуйте, {{user.name}}!\n\nДля вашей учетной записи выдан новый пароль:\n{{password}}\n\nПосле входа рекомендуем сменить его в профиле.",
            [.. UserVariables, new("password", "Выданный пароль")]),
        new(
            NotificationTemplateKeys.ContractTerminationClient,
            "Расторжение договора для клиента",
            "Письмо клиенту при расторжении договора.",
            "Договор № {{contract.number}} расторгнут",
            "Договор № {{contract.number}} расторгнут.\n\nДата расторжения: {{contract.terminationDate}}.\n{{contract.reasonBlock}}{{contract.termsBlock}}{{contract.financialTermsBlock}}",
            [
                new("contract.number", "Номер договора"),
                new("contract.terminationDate", "Дата расторжения"),
                new("contract.reason", "Причина расторжения"),
                new("contract.terminationTerms", "Условия расторжения"),
                new("contract.financialTerms", "Финансовые условия"),
                new("contract.reasonBlock", "Строка причины, если заполнена"),
                new("contract.termsBlock", "Строка условий, если заполнена"),
                new("contract.financialTermsBlock", "Строка финансовых условий, если заполнена")
            ]),
        new(
            NotificationTemplateKeys.TaskAssigned,
            "Новая задача",
            "Уведомление исполнителю и участникам при назначении задачи.",
            "{{task.assignedSubject}}",
            "{{actor.name}} назначил задачу: {{task.title}}\n\nОбъект: {{task.objectName}}\nСрок: {{task.dueAt}}\nИсполнитель: {{task.assigneeName}}\n{{task.assignmentDetails}}",
            TaskVariables),
        new(
            NotificationTemplateKeys.TaskReminder,
            "Напоминание по задаче",
            "Плановое уведомление о сроках и просрочках задач.",
            "{{reminder.subject}}",
            "{{reminder.body}}",
            [.. TaskVariables, new("reminder.subject", "Стандартная тема напоминания"), new("reminder.body", "Стандартный текст напоминания")]),
        new(
            NotificationTemplateKeys.TaskCompleted,
            "Задача выполнена",
            "Уведомление постановщику о завершении задачи.",
            "Задача выполнена: {{task.title}}",
            "Задача выполнена: {{task.title}}\n\nИсполнитель: {{task.assigneeName}}\nОбъект: {{task.objectName}}",
            TaskVariables),
        new(
            NotificationTemplateKeys.TaskCancelled,
            "Задача отменена",
            "Уведомление участникам при отмене или удалении календарной задачи.",
            "Задача отменена: {{task.title}}",
            "{{actor.name}} отменил(а) календарную задачу: {{task.title}}\n\nОбъект: {{task.objectName}}\nСрок: {{task.dueAt}}\nДействие: {{task.cancelAction}}",
            [.. TaskVariables, new("task.cancelAction", "Как отменена задача")]),
        new(
            NotificationTemplateKeys.TaskScheduleConflict,
            "Конфликт расписания",
            "Уведомление о пересечении задачи с другими событиями.",
            "Конфликт расписания: {{task.title}}",
            "{{actor.name}} сохранил задачу, которая пересекается с другими событиями.\n\nЗадача: {{task.title}}\nСрок: {{task.dueAt}}\n\n{{conflicts}}",
            [.. TaskVariables, new("conflicts", "Список конфликтующих задач")]),
        new(
            NotificationTemplateKeys.TaskApprovalRequested,
            "Задача ожидает согласования",
            "Уведомление согласующему о задаче на проверке.",
            "Задача ожидает согласования: {{task.title}}",
            "{{actor.name}} отправил задачу на согласование.\n\nЗадача: {{task.title}}\nСогласующий: {{approval.approverName}}\nОбъект: {{task.objectName}}",
            [.. TaskVariables, new("approval.approverName", "Текущий согласующий")]),
        new(
            NotificationTemplateKeys.TaskApproved,
            "Задача согласована",
            "Уведомление после подтверждения результата задачи.",
            "Задача согласована: {{task.title}}",
            "{{actor.name}} согласовал задачу: {{task.title}}\n\nРешение: {{approval.resolution}}",
            [.. TaskVariables, new("approval.resolution", "Комментарий согласующего")]),
        new(
            NotificationTemplateKeys.TaskRevisionRequested,
            "Возврат на доработку",
            "Уведомление при возврате задачи исполнителю.",
            "Задача возвращена на доработку: {{task.title}}",
            "{{actor.name}} вернул задачу на доработку: {{task.title}}\n\nКомментарий: {{approval.resolution}}",
            [.. TaskVariables, new("approval.resolution", "Комментарий согласующего")]),
        new(
            NotificationTemplateKeys.TaskApprovalOverdue,
            "Просрочено согласование",
            "Уведомление о просроченном сроке согласования задачи.",
            "Просрочено согласование: {{task.title}}",
            "Срок согласования задачи истек.\n\nЗадача: {{task.title}}\nСогласующий: {{approval.approverName}}\nСрок согласования: {{approval.dueAt}}\nОбъект: {{task.objectName}}",
            [.. TaskVariables, new("approval.approverName", "Текущий согласующий"), new("approval.dueAt", "Срок согласования")]),
        new(
            NotificationTemplateKeys.CalendarAccessChanged,
            "Изменена видимость календарей",
            "Уведомление сотрудникам роли, когда администратор меняет список доступных календарей.",
            "Изменена видимость календарей",
            "{{actor.name}} изменил(а) список календарей сотрудников, доступных вашей роли.\n\nДобавлены: {{calendar.added}}\nУбраны: {{calendar.removed}}",
            [new("actor.name", "Кто выполнил действие"), new("calendar.added", "Добавленные календари"), new("calendar.removed", "Убранные календари")]),
        new(
            NotificationTemplateKeys.TaskChanged,
            "Изменения по задаче",
            "Уведомление участникам при изменении важных полей задачи.",
            "{{task.changeSubject}}",
            "{{actor.name}} изменил задачу: {{task.title}}\n\nИзменения:\n{{task.changes}}\n\n{{task.commentBlock}}",
            [.. TaskVariables, new("task.changes", "Список изменений"), new("task.commentBlock", "Комментарий к изменению, если заполнен"), new("task.changeSubject", "Тема уведомления с учетом типа изменения")]),
        new(
            NotificationTemplateKeys.TaskComment,
            "Комментарий по задаче",
            "Уведомление о новом комментарии в задаче.",
            "Комментарий по задаче: {{task.title}}",
            "{{comment.authorName}} добавил комментарий к задаче: {{task.title}}\n\n{{comment.text}}",
            [.. TaskVariables, new("comment.authorName", "Автор комментария"), new("comment.text", "Текст комментария")]),
        new(
            NotificationTemplateKeys.CaseInactivityControl,
            "Автоконтроль дела",
            "Уведомление о контрольной задаче по делу без активности.",
            "Автоконтроль дела: {{task.objectName}}",
            "По делу {{task.objectName}} давно не было активных задач.\n\nСоздана контрольная задача: {{task.title}}\nПериод без активной задачи: {{case.daysWithoutActiveTask}} дн.",
            [.. TaskVariables, new("case.daysWithoutActiveTask", "Дней без активной задачи")])
    ];

    public static NotificationTemplateDefinition? Find(string key)
        => All.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));

    public static string Render(string template, IReadOnlyDictionary<string, string?> values)
        => PlaceholderRegex.Replace(template, match =>
        {
            var key = match.Groups[1].Value;
            return values.TryGetValue(key, out var value) ? value ?? string.Empty : string.Empty;
        }).Trim();

    public static Dictionary<string, string?> MergeValues(params IReadOnlyDictionary<string, string?>[] sources)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            foreach (var pair in source)
            {
                result[pair.Key] = pair.Value;
            }
        }

        return result;
    }

    public static string NormalizeLineBreaks(string value)
        => value.Replace("\r\n", "\n").Replace('\r', '\n');

    public static string CompactBlankLines(string value)
    {
        var lines = NormalizeLineBreaks(value).Split('\n');
        var builder = new StringBuilder();
        var blankCount = 0;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
            {
                blankCount++;
                if (blankCount > 1)
                {
                    continue;
                }
            }
            else
            {
                blankCount = 0;
            }

            builder.AppendLine(line);
        }

        return builder.ToString().Trim();
    }
}
