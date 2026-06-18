using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services.Notifications;

public sealed record NotificationChannelDefinition(
    string Key,
    string Label,
    string Description,
    string Status,
    bool IsEnabled,
    bool IsSendable,
    bool SupportsUserPreferences,
    bool SupportsDeliveryLog,
    bool RequiresProvider,
    bool IsFuture);

public static class NotificationChannels
{
    public const string InApp = "inApp";
    public const string Toast = "toast";
    public const string Email = "email";
    public const string Telegram = "telegram";
    public const string WebPush = "webPush";
    public const string Sms = "sms";
    public const string Max = "max";
    public static readonly IReadOnlyList<string> All = [InApp, Toast, WebPush, Email, Telegram];
    public static readonly IReadOnlyList<string> External = [Email, Telegram, WebPush];
    public static readonly IReadOnlyList<string> Future = [Sms, Max];
    public static readonly IReadOnlyList<string> Known = [InApp, Toast, WebPush, Email, Telegram, Sms, Max];
    public static readonly IReadOnlyList<NotificationChannelDefinition> Definitions =
    [
        new(InApp, "Центр уведомлений", "Внутренние уведомления в CRM, счетчик и центр уведомлений.", "active", true, true, true, false, false, false),
        new(Toast, "Всплывающие уведомления", "Моментальные уведомления поверх интерфейса CRM при открытой вкладке.", "active", true, false, true, false, false, false),
        new(WebPush, "Web push", "Браузерные push-уведомления для важных событий, когда вкладка CRM закрыта или не на экране.", "active", true, true, true, true, false, false),
        new(Email, "Email", "Письма через настроенных SMTP-отправителей.", "active", true, true, true, true, true, false),
        new(Telegram, "Telegram", "Сообщения через подключенных Telegram-ботов и привязанные чаты пользователей.", "active", true, true, true, true, true, false),
        new(Sms, "SMS", "SMS-уведомления. Адаптер подготовлен в модели каналов, провайдер будет выбран отдельно.", "planned", false, false, false, true, true, true),
        new(Max, "MAX", "Уведомления в мессенджер MAX. Канал зарезервирован как будущая интеграция без привязки к конкретному API.", "planned", false, false, false, true, true, true)
    ];
}

public static class NotificationPriorities
{
    public const string Critical = "critical";
    public const string High = "high";
    public const string Normal = "normal";
    public const string Silent = "silent";

    public static bool IsCritical(string? value)
        => string.Equals(value, Critical, StringComparison.OrdinalIgnoreCase);

    public static bool IsHighOrCritical(string? value)
        => IsCritical(value) || string.Equals(value, High, StringComparison.OrdinalIgnoreCase);

    public static bool IsSilent(string? value)
        => string.Equals(value, Silent, StringComparison.OrdinalIgnoreCase);
}

public static class UserNotificationDomains
{
    public const string Task = "task";
    public const string Calendar = "calendar";
    public const string Note = "note";
    public const string Payment = "payment";
    public const string Invoice = "invoice";
    public const string Act = "act";
    public const string Contract = "contract";
    public const string Case = "case";
    public const string Court = "court";
    public const string System = "system";

    public static string Resolve(string? type)
        => type switch
        {
            UserNotificationTypes.TaskAssigned
                or UserNotificationTypes.TaskReminder
                or UserNotificationTypes.TaskChanged
                or UserNotificationTypes.TaskComment
                or UserNotificationTypes.TaskCompleted
                or UserNotificationTypes.TaskCancelled
                or UserNotificationTypes.TaskScheduleConflict
                or UserNotificationTypes.TaskApprovalRequested
                or UserNotificationTypes.TaskApproved
                or UserNotificationTypes.TaskRevisionRequested
                or UserNotificationTypes.TaskApprovalOverdue => Task,
            UserNotificationTypes.CalendarAccessChanged => Calendar,
            UserNotificationTypes.NoteMention
                or UserNotificationTypes.NoteAssigned
                or UserNotificationTypes.NoteReminder
                or UserNotificationTypes.NoteImportantChanged => Note,
            UserNotificationTypes.PaymentAssigned
                or UserNotificationTypes.PaymentChanged
                or UserNotificationTypes.PaymentAdvanceReviewRequired
                or UserNotificationTypes.PaymentAdvanceReviewEscalation => Payment,
            UserNotificationTypes.InvoiceCreated
                or UserNotificationTypes.InvoiceChanged => Invoice,
            UserNotificationTypes.ActCreated
                or UserNotificationTypes.ActChanged => Act,
            UserNotificationTypes.ContractCreated
                or UserNotificationTypes.ContractChanged
                or UserNotificationTypes.ContractExpiring => Contract,
            UserNotificationTypes.CaseInactivityControl
                or UserNotificationTypes.CaseAccessRequested
                or UserNotificationTypes.CaseAccessResolved
                or UserNotificationTypes.CaseProceduralDeadline
                or UserNotificationTypes.CaseDocument
                or UserNotificationTypes.CaseInactivityReminder => Case,
            UserNotificationTypes.CaseCourtEvent
                or UserNotificationTypes.CourtHearing
                or UserNotificationTypes.CourtStatusChanged
                or UserNotificationTypes.CourtDecisionPublished
                or UserNotificationTypes.CourtSyncFailed => Court,
            _ => System
        };

    public static string Label(string? domain)
        => domain switch
        {
            Task => "Задачи",
            Calendar => "Календарь",
            Note => "Заметки",
            Payment => "Платежи",
            Invoice => "Счета",
            Act => "Акты",
            Contract => "Договоры",
            Case => "Дела",
            Court => "Суды",
            _ => "Система"
        };
}

public static class NotificationDeliveryStatus
{
    public const string Pending = "pending";
    public const string Sent = "sent";
    public const string Skipped = "skipped";
    public const string Failed = "failed";
}

public static class TelegramBotUpdateModes
{
    public const string Disabled = "disabled";
    public const string Webhook = "webhook";
    public const string Polling = "polling";

    public static readonly IReadOnlyList<string> All = [Disabled, Webhook, Polling];
}

public static class NotificationPurposes
{
    public const string TelegramAuth = "telegramAuth";
    public const string RegistrationUser = "registrationUser";
    public const string RegistrationAdmin = "registrationAdmin";
    public const string PasswordIssue = "passwordIssue";
    public const string TaskReminder = "taskReminder";
    public const string ContractClient = "contractClient";
    public const string ManualTest = "manualTest";

    public static readonly IReadOnlyList<string> All =
    [
        TelegramAuth,
        RegistrationUser,
        RegistrationAdmin,
        PasswordIssue,
        TaskReminder,
        ContractClient,
        ManualTest
    ];
}

public sealed record NotificationPurposeDefinition(
    string Key,
    string Label,
    string Description);

public static class UserNotificationTypes
{
    public const string TaskAssigned = "taskAssigned";
    public const string TaskReminder = "taskReminder";
    public const string TaskChanged = "taskChanged";
    public const string TaskComment = "taskComment";
    public const string TaskCompleted = "taskCompleted";
    public const string TaskCancelled = "taskCancelled";
    public const string TaskScheduleConflict = "taskScheduleConflict";
    public const string TaskApprovalRequested = "taskApprovalRequested";
    public const string TaskApproved = "taskApproved";
    public const string TaskRevisionRequested = "taskRevisionRequested";
    public const string TaskApprovalOverdue = "taskApprovalOverdue";
    public const string CalendarAccessChanged = "calendarAccessChanged";
    public const string CaseInactivityControl = "caseInactivityControl";
    public const string CaseAccessRequested = "caseAccessRequested";
    public const string CaseAccessResolved = "caseAccessResolved";
    public const string CaseCourtEvent = "caseCourtEvent";
    public const string CourtHearing = "courtHearing";
    public const string CourtStatusChanged = "courtStatusChanged";
    public const string CourtDecisionPublished = "courtDecisionPublished";
    public const string CourtSyncFailed = "courtSyncFailed";
    public const string CaseProceduralDeadline = "caseProceduralDeadline";
    public const string CaseDocument = "caseDocument";
    public const string CaseInactivityReminder = "caseInactivityReminder";
    public const string PaymentAssigned = "paymentAssigned";
    public const string PaymentChanged = "paymentChanged";
    public const string InvoiceCreated = "invoiceCreated";
    public const string InvoiceChanged = "invoiceChanged";
    public const string ActCreated = "actCreated";
    public const string ActChanged = "actChanged";
    public const string ContractCreated = "contractCreated";
    public const string ContractChanged = "contractChanged";
    public const string ContractExpiring = "contractExpiring";
    public const string PaymentAdvanceReviewRequired = "paymentAdvanceReviewRequired";
    public const string PaymentAdvanceReviewEscalation = "paymentAdvanceReviewEscalation";
    public const string NoteMention = "noteMention";
    public const string NoteAssigned = "noteAssigned";
    public const string NoteReminder = "noteReminder";
    public const string NoteImportantChanged = "noteImportantChanged";
    public const string SystemAdminWarning = "systemAdminWarning";

    public static readonly IReadOnlyList<UserNotificationTypeDefinition> All =
    [
        new(TaskAssigned, "Новая задача", "Назначение новой задачи исполнителю или участникам.", "blue", NotificationPriorities.High, NotificationChannels.All, true, false),
        new(TaskReminder, "Напоминание по задаче", "Сроки, просрочки и плановые напоминания по задачам.", "amber", NotificationPriorities.Critical, NotificationChannels.All, true, false),
        new(TaskChanged, "Изменение задачи", "Изменения статусов, сроков, исполнителей и других полей задачи.", "blue", "normal", NotificationChannels.All, false, true),
        new(TaskComment, "Комментарий по задаче", "Новые комментарии и сообщения по задаче.", "emerald", "normal", NotificationChannels.All, true, true),
        new(TaskCompleted, "Задача выполнена", "Уведомление постановщику о завершении задачи.", "blue", NotificationPriorities.Silent, NotificationChannels.All, true, false),
        new(TaskCancelled, "Задача отменена", "Календарная задача отменена или удалена.", "orange", "high", NotificationChannels.All, true, false),
        new(TaskScheduleConflict, "Конфликт расписания", "Задача пересекается по времени с другой активной задачей исполнителя.", "rose", "high", NotificationChannels.All, true, false),
        new(TaskApprovalRequested, "Ожидает согласования", "Задача отправлена согласующему на решение.", "violet", "high", NotificationChannels.All, true, false),
        new(TaskApproved, "Задача согласована", "Согласующий подтвердил результат задачи.", "emerald", "normal", NotificationChannels.All, false, false),
        new(TaskRevisionRequested, "Возврат на доработку", "Согласующий вернул задачу исполнителю на доработку.", "orange", "high", NotificationChannels.All, false, false),
        new(TaskApprovalOverdue, "Просрочено согласование", "Срок согласования задачи истек, требуется решение согласующего или контроль исполнителя.", "rose", NotificationPriorities.Critical, NotificationChannels.All, true, false),
        new(CalendarAccessChanged, "Изменена видимость календарей", "Администратор изменил, календари каких сотрудников доступны пользователям роли.", "violet", "high", NotificationChannels.All, true, false),
        new(CaseInactivityControl, "Автоконтроль дела", "По делу долго не было активных задач, создана контрольная задача.", "amber", "high", NotificationChannels.All, true, false),
        new(CaseAccessRequested, "Запрос доступа к делу", "Исполнитель запросил доступ к скрытому делу.", "violet", "high", NotificationChannels.All, false, false),
        new(CaseAccessResolved, "Решение по доступу к делу", "Руководитель разрешил или отклонил доступ к скрытому делу.", "emerald", "normal", NotificationChannels.All, false, false),
        new(CaseCourtEvent, "Судебное событие", "По делу добавлено или обновлено судебное заседание, движение или судебный акт.", "cyan", "high", NotificationChannels.All, false, false),
        new(CourtHearing, "Судебное заседание", "Назначено, перенесено или обновлено судебное заседание по делу.", "cyan", "high", NotificationChannels.All, true, false),
        new(CourtStatusChanged, "Изменение судебного статуса", "Изменился статус судебного производства или движение дела в суде.", "blue", "high", NotificationChannels.All, true, false),
        new(CourtDecisionPublished, "Публикация решения", "По делу опубликовано решение, определение или другой судебный акт.", "emerald", "high", NotificationChannels.All, true, false),
        new(CourtSyncFailed, "Ошибка судебной синхронизации", "Судебный источник не удалось проверить или обновить автоматически.", "rose", NotificationPriorities.Critical, NotificationChannels.All, true, false),
        new(CaseProceduralDeadline, "Процессуальный срок", "По делу добавлен, перенесен, закрыт или приближается процессуальный срок.", "amber", "high", NotificationChannels.All, false, false),
        new(CaseDocument, "Документы дела", "По делу загружен, сформирован, обновлен или удален документ.", "emerald", NotificationPriorities.Silent, NotificationChannels.All, false, false),
        new(CaseInactivityReminder, "Напоминание о неактивности", "По делу давно не было значимой активности, требуется проверка ответственного.", "amber", "high", NotificationChannels.All, true, false),
        new(PaymentAssigned, "Назначен платеж", "Ответственного добавили к платежу, который требует контроля.", "blue", "high", NotificationChannels.All, true, false),
        new(PaymentChanged, "Изменение платежа", "Изменились важные параметры платежа: статус, дата, сумма или источник.", "amber", "high", NotificationChannels.All, true, false),
        new(InvoiceCreated, "Создан счет", "Ответственному сотруднику доступен новый счет для контроля.", "blue", "high", NotificationChannels.All, true, false),
        new(InvoiceChanged, "Изменение счета", "Изменился важный статус счета: отправка, подпись, оплата, просрочка или отмена.", "amber", "high", NotificationChannels.All, true, false),
        new(ActCreated, "Создан акт", "Ответственному сотруднику доступен новый акт для контроля.", "blue", "high", NotificationChannels.All, true, false),
        new(ActChanged, "Изменение акта", "Изменился важный статус акта: корректировка, подписание, закрытие или отмена.", "amber", "high", NotificationChannels.All, true, false),
        new(ContractCreated, "Создан договор", "Ответственным сотрудникам доступен новый договор для контроля.", "blue", "high", NotificationChannels.All, true, false),
        new(ContractChanged, "Изменение договора", "Изменился важный статус, срок или условие договора.", "amber", "high", NotificationChannels.All, true, false),
        new(ContractExpiring, "Истекает срок договора", "Срок действия договора подходит к концу и требует внимания ответственных.", "rose", "high", NotificationChannels.All, true, false),
        new(PaymentAdvanceReviewRequired, "Контроль аванса", "Входящий оплаченный платеж не привязан к счету или акту и требует решения по авансу.", "rose", NotificationPriorities.Critical, NotificationChannels.All, true, false),
        new(PaymentAdvanceReviewEscalation, "Просроченный аванс", "Нераспределенный аванс слишком долго ожидает решения и требует внимания руководителя.", "rose", NotificationPriorities.Critical, NotificationChannels.All, true, false),
        new(NoteMention, "Упоминание в заметке", "Сотрудника упомянули на доске заметок.", "cyan", "high", NotificationChannels.All, true, false),
        new(NoteAssigned, "Назначена заметка", "Сотрудника назначили ответственным по заметке.", "violet", "high", NotificationChannels.All, true, false),
        new(NoteReminder, "Напоминание по заметке", "Наступил срок напоминания по назначенной заметке.", "amber", "high", NotificationChannels.All, true, false),
        new(NoteImportantChanged, "Важная заметка", "На общей доске закрепили, изменили или удалили важную заметку.", "orange", "high", NotificationChannels.All, true, false),
        new(SystemAdminWarning, "Системное предупреждение", "Администраторам требуется обратить внимание на фоновый процесс, интеграцию или настройку.", "rose", NotificationPriorities.Critical, NotificationChannels.All, true, false)
    ];

    public static readonly IReadOnlyList<string> RequiresActionTypes =
    [
        TaskAssigned,
        TaskReminder,
        TaskCancelled,
        TaskScheduleConflict,
        TaskApprovalRequested,
        TaskRevisionRequested,
        TaskApprovalOverdue,
        CalendarAccessChanged,
        CaseInactivityControl,
        CaseAccessRequested,
        CaseCourtEvent,
        CourtHearing,
        CourtStatusChanged,
        CourtDecisionPublished,
        CourtSyncFailed,
        CaseProceduralDeadline,
        CaseInactivityReminder,
        NoteMention,
        NoteAssigned,
        NoteReminder,
        NoteImportantChanged,
        PaymentAssigned,
        PaymentChanged,
        InvoiceCreated,
        InvoiceChanged,
        ActCreated,
        ActChanged,
        ContractCreated,
        ContractChanged,
        ContractExpiring,
        PaymentAdvanceReviewRequired,
        PaymentAdvanceReviewEscalation,
        SystemAdminWarning
    ];

    public static bool IsKnown(string? value)
        => All.Any(item => string.Equals(item.Key, value, StringComparison.OrdinalIgnoreCase));

    public static UserNotificationTypeDefinition? Find(string? value)
        => All.FirstOrDefault(item => string.Equals(item.Key, value, StringComparison.OrdinalIgnoreCase));

    public static bool RequiresAction(string? value)
        => RequiresActionTypes.Contains(value ?? string.Empty, StringComparer.OrdinalIgnoreCase);
}

public sealed record UserNotificationTypeDefinition(
    string Key,
    string Label,
    string Description,
    string Tone,
    string Priority,
    IReadOnlyList<string> Channels,
    bool Deduplicate,
    bool Digestible)
{
    public string Domain => UserNotificationDomains.Resolve(Key);
    public string DomainLabel => UserNotificationDomains.Label(Domain);
    public IReadOnlyList<string> EffectiveChannels => NotificationPriorities.IsSilent(Priority)
        ? Channels.Where(channel => string.Equals(channel, NotificationChannels.InApp, StringComparison.OrdinalIgnoreCase)).ToList()
        : Channels;
    public bool ShowToastByDefault => EffectiveChannels.Contains(NotificationChannels.Toast, StringComparer.OrdinalIgnoreCase);
    public string ToastPriority => Priority;
    public bool PushEligible => EffectiveChannels.Contains(NotificationChannels.WebPush, StringComparer.OrdinalIgnoreCase);
    public string PushPriority => Priority;
    public int GroupingWindowSeconds => Priority switch
    {
        var value when NotificationPriorities.IsCritical(value) => 0,
        var value when NotificationPriorities.IsSilent(value) => 600,
        _ => Digestible ? 300 : Deduplicate ? 60 : 0
    };
    public bool RequiresAction => UserNotificationTypes.RequiresAction(Key);
}

public sealed record NotificationEventEnvelope(
    int RecipientUserId,
    string Type,
    string EventKey,
    string Title,
    string BodyPreview,
    string ObjectType,
    int? ObjectId,
    int? TaskJournalItemId,
    string? Tone = null,
    object? Metadata = null,
    TaskJournalItem? Task = null,
    IReadOnlyList<string>? Channels = null,
    string? ActionUrl = null)
{
    public UserNotificationTypeDefinition? TypeDefinition => UserNotificationTypes.Find(Type);
    public string EffectiveType => TypeDefinition?.Key ?? Type.Trim();
    public string Priority => TypeDefinition?.Priority ?? NotificationPriorities.Normal;
    public string Domain => TypeDefinition?.Domain ?? UserNotificationDomains.System;
    public string EffectiveTone => string.IsNullOrWhiteSpace(Tone) ? TypeDefinition?.Tone ?? "blue" : Tone.Trim();
    public IReadOnlyList<string> EffectiveChannels
        => Channels is { Count: > 0 }
            ? NormalizeChannelsByPriority(Channels, Priority)
            : TypeDefinition?.EffectiveChannels ?? NotificationChannels.All;

    private static IReadOnlyList<string> NormalizeChannelsByPriority(IReadOnlyList<string> channels, string priority)
        => NotificationPriorities.IsSilent(priority)
            ? channels.Where(channel => string.Equals(channel, NotificationChannels.InApp, StringComparison.OrdinalIgnoreCase)).ToList()
            : channels;
}

public static class UserNotificationPreferenceSettings
{
    public const string SettingKey = "notifications.typePreferences.v1";
    public static readonly UserNotificationPreferenceSnapshot Empty = new([], new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));

    public static IReadOnlyList<string> ParseDisabledTypes(string? rawValue)
        => Parse(rawValue).DisabledTypes;

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseDisabledChannels(string? rawValue)
        => Parse(rawValue).DisabledChannels;

    public static UserNotificationPreferenceSnapshot Parse(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return Empty;
        }

        try
        {
            var storage = JsonSerializer.Deserialize<UserNotificationPreferenceStorage>(rawValue, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return Normalize(storage);
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    public static string Serialize(UserNotificationPreferenceSnapshot preferences)
        => JsonSerializer.Serialize(new UserNotificationPreferenceStorage
        {
            DisabledTypes = NormalizeTypes(preferences.DisabledTypes).ToList(),
            DisabledChannels = NormalizeDisabledChannels(preferences.DisabledChannels)
                .ToDictionary(
                    item => item.Key,
                    item => item.Value.ToList(),
                    StringComparer.OrdinalIgnoreCase)
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static bool IsTypeEnabled(string? rawValue, string? type)
        => IsTypeEnabled(Parse(rawValue), type);

    public static bool IsTypeEnabled(UserNotificationPreferenceSnapshot preferences, string? type)
    {
        var normalizedType = NormalizeType(type);
        return string.IsNullOrWhiteSpace(normalizedType)
               || !preferences.DisabledTypes.Contains(normalizedType, StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsChannelEnabled(string? rawValue, string? type, string? channel)
        => IsChannelEnabled(Parse(rawValue), type, channel);

    public static bool IsChannelEnabled(UserNotificationPreferenceSnapshot preferences, string? type, string? channel)
    {
        var normalizedType = NormalizeType(type);
        var normalizedChannel = NormalizeChannel(channel);
        if (string.IsNullOrWhiteSpace(normalizedType) || string.IsNullOrWhiteSpace(normalizedChannel))
        {
            return true;
        }

        if (!IsTypeEnabled(preferences, normalizedType))
        {
            return false;
        }

        return !preferences.DisabledChannels.TryGetValue(normalizedType, out var disabledChannels)
               || !disabledChannels.Contains(normalizedChannel, StringComparer.OrdinalIgnoreCase);
    }

    private static UserNotificationPreferenceSnapshot Normalize(UserNotificationPreferenceStorage? storage)
        => new(
            NormalizeTypes(storage?.DisabledTypes),
            NormalizeDisabledChannels(storage?.DisabledChannels));

    private static IReadOnlyList<string> NormalizeTypes(IEnumerable<string>? values)
        => (values ?? [])
            .Select(NormalizeType)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> NormalizeDisabledChannels(Dictionary<string, List<string>>? values)
        => NormalizeDisabledChannelsCore(values?.Select(item => new KeyValuePair<string, IEnumerable<string>>(item.Key, item.Value)));

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> NormalizeDisabledChannels(IReadOnlyDictionary<string, IReadOnlyList<string>>? values)
        => NormalizeDisabledChannelsCore(values?.Select(item => new KeyValuePair<string, IEnumerable<string>>(item.Key, item.Value)));

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> NormalizeDisabledChannelsCore(IEnumerable<KeyValuePair<string, IEnumerable<string>>>? values)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (type, channels) in values ?? [])
        {
            var normalizedType = NormalizeType(type);
            if (string.IsNullOrWhiteSpace(normalizedType))
            {
                continue;
            }

            var normalizedChannels = NormalizeChannels(channels);
            if (normalizedChannels.Count > 0)
            {
                result[normalizedType] = normalizedChannels;
            }
        }

        return result;
    }

    private static IReadOnlyList<string> NormalizeChannels(IEnumerable<string>? values)
        => (values ?? [])
            .Select(NormalizeChannel)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string? NormalizeType(string? value)
        => UserNotificationTypes.Find(value)?.Key;

    private static string? NormalizeChannel(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return NotificationChannels.All.FirstOrDefault(channel => string.Equals(channel, normalized, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class UserNotificationPreferenceStorage
    {
        public List<string> DisabledTypes { get; set; } = [];
        public Dictionary<string, List<string>> DisabledChannels { get; set; } = [];
    }
}

public sealed record UserNotificationPreferenceSnapshot(
    IReadOnlyList<string> DisabledTypes,
    IReadOnlyDictionary<string, IReadOnlyList<string>> DisabledChannels);

public static class UserNotificationDoNotDisturbSettings
{
    public const string SettingKey = "notifications.doNotDisturb.v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly IReadOnlyList<int> DefaultWorkDays = [1, 2, 3, 4, 5];
    private static readonly IReadOnlySet<string> MutedChannels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        NotificationChannels.Toast,
        NotificationChannels.WebPush,
        NotificationChannels.Email,
        NotificationChannels.Telegram
    };

    public static readonly UserNotificationDoNotDisturbSnapshot Disabled = new(
        Enabled: false,
        WorkStart: "09:00",
        WorkEnd: "18:00",
        MuteWeekends: true,
        VacationUntil: null,
        AllowCritical: true,
        WorkDays: DefaultWorkDays);

    public static UserNotificationDoNotDisturbSnapshot Parse(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return Disabled;
        }

        try
        {
            var storage = JsonSerializer.Deserialize<UserNotificationDoNotDisturbStorage>(rawValue, JsonOptions);
            return Normalize(storage);
        }
        catch (JsonException)
        {
            return Disabled;
        }
    }

    public static string Serialize(UserNotificationDoNotDisturbSnapshot settings)
        => JsonSerializer.Serialize(new UserNotificationDoNotDisturbStorage
        {
            Enabled = settings.Enabled,
            WorkStart = NormalizeTime(settings.WorkStart, Disabled.WorkStart),
            WorkEnd = NormalizeTime(settings.WorkEnd, Disabled.WorkEnd),
            MuteWeekends = settings.MuteWeekends,
            VacationUntil = NormalizeDate(settings.VacationUntil),
            AllowCritical = settings.AllowCritical,
            WorkDays = NormalizeWorkDays(settings.WorkDays).ToList()
        }, JsonOptions);

    public static bool IsChannelMuted(
        UserNotificationDoNotDisturbSnapshot settings,
        UserNotificationTypeDefinition? type,
        string? channel,
        DateTime nowUtc,
        TimeZoneInfo timeZone)
    {
        if (!settings.Enabled || !IsMutedChannel(channel))
        {
            return false;
        }

        if (settings.AllowCritical && IsCritical(type))
        {
            return false;
        }

        var localNow = CalendarTimeZoneHelper.ToUserLocal(nowUtc, timeZone);
        if (IsVacationDay(settings, localNow.Date))
        {
            return true;
        }

        if (!IsWorkDay(settings, localNow.DayOfWeek))
        {
            return settings.MuteWeekends;
        }

        return !IsWithinWorkWindow(settings, localNow.TimeOfDay);
    }

    public static bool IsCritical(UserNotificationTypeDefinition? type)
        => NotificationPriorities.IsCritical(type?.Priority);

    private static UserNotificationDoNotDisturbSnapshot Normalize(UserNotificationDoNotDisturbStorage? storage)
        => new(
            Enabled: storage?.Enabled ?? Disabled.Enabled,
            WorkStart: NormalizeTime(storage?.WorkStart, Disabled.WorkStart),
            WorkEnd: NormalizeTime(storage?.WorkEnd, Disabled.WorkEnd),
            MuteWeekends: storage?.MuteWeekends ?? Disabled.MuteWeekends,
            VacationUntil: NormalizeDate(storage?.VacationUntil),
            AllowCritical: storage?.AllowCritical ?? Disabled.AllowCritical,
            WorkDays: NormalizeWorkDays(storage?.WorkDays));

    private static bool IsMutedChannel(string? channel)
        => MutedChannels.Contains((channel ?? string.Empty).Trim());

    private static bool IsVacationDay(UserNotificationDoNotDisturbSnapshot settings, DateTime localDate)
        => TryParseDate(settings.VacationUntil, out var vacationUntil)
           && localDate.Date <= vacationUntil.Date;

    private static bool IsWorkDay(UserNotificationDoNotDisturbSnapshot settings, DayOfWeek dayOfWeek)
    {
        var normalizedDay = dayOfWeek == DayOfWeek.Sunday ? 7 : (int)dayOfWeek;
        return settings.WorkDays.Contains(normalizedDay);
    }

    private static bool IsWithinWorkWindow(UserNotificationDoNotDisturbSnapshot settings, TimeSpan localTime)
    {
        var start = ParseTime(settings.WorkStart, Disabled.WorkStart);
        var end = ParseTime(settings.WorkEnd, Disabled.WorkEnd);
        if (start == end)
        {
            return true;
        }

        return start < end
            ? localTime >= start && localTime < end
            : localTime >= start || localTime < end;
    }

    private static IReadOnlyList<int> NormalizeWorkDays(IEnumerable<int>? values)
    {
        var days = (values ?? DefaultWorkDays)
            .Where(value => value is >= 1 and <= 7)
            .Distinct()
            .OrderBy(value => value)
            .ToList();

        return days.Count > 0 ? days : DefaultWorkDays;
    }

    private static string NormalizeTime(string? value, string fallback)
        => TimeSpan.TryParseExact((value ?? string.Empty).Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out var time)
            ? time.ToString(@"hh\:mm", CultureInfo.InvariantCulture)
            : fallback;

    private static TimeSpan ParseTime(string? value, string fallback)
        => TimeSpan.TryParseExact(NormalizeTime(value, fallback), @"hh\:mm", CultureInfo.InvariantCulture, out var time)
            ? time
            : TimeSpan.Zero;

    private static string? NormalizeDate(string? value)
        => TryParseDate(value, out var date)
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : null;

    private static bool TryParseDate(string? value, out DateTime date)
        => DateTime.TryParseExact(
            (value ?? string.Empty).Trim(),
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);

    private sealed class UserNotificationDoNotDisturbStorage
    {
        public bool Enabled { get; set; }
        public string? WorkStart { get; set; }
        public string? WorkEnd { get; set; }
        public bool? MuteWeekends { get; set; }
        public string? VacationUntil { get; set; }
        public bool? AllowCritical { get; set; }
        public List<int>? WorkDays { get; set; }
    }
}

public sealed record UserNotificationDoNotDisturbSnapshot(
    bool Enabled,
    string WorkStart,
    string WorkEnd,
    bool MuteWeekends,
    string? VacationUntil,
    bool AllowCritical,
    IReadOnlyList<int> WorkDays);

public sealed record RoleNotificationPreferenceDefaultProfile(
    string Key,
    string Label,
    string Description,
    UserNotificationPreferenceSnapshot Preferences);

public static class RoleNotificationPreferenceDefaults
{
    private static readonly IReadOnlyList<string> ExternalChannels = [NotificationChannels.Email, NotificationChannels.Telegram];

    public static RoleNotificationPreferenceDefaultProfile Resolve(string? roleName, bool isSystemRole = false)
    {
        var normalized = NormalizeRoleName(roleName);
        if (isSystemRole || normalized == "admin" || normalized.Contains("админист", StringComparison.Ordinal))
        {
            return BuildProfile("admin", "Администратор", "Все уведомления включены, внешние каналы остаются доступны для контроля системы.", [UserNotificationDomains.System]);
        }

        if (ContainsAny(normalized, "секрет"))
        {
            return BuildProfile("secretary", "Секретарь", "Сразу видны задачи, календарь, заметки, дела и судебные изменения; внешние каналы оставлены для срочных рабочих событий.", [
                UserNotificationDomains.Task,
                UserNotificationDomains.Calendar,
                UserNotificationDomains.Note,
                UserNotificationDomains.Case,
                UserNotificationDomains.Court
            ]);
        }

        if (ContainsAny(normalized, "юрист", "адвокат"))
        {
            return BuildProfile("lawyer", "Юрист", "Приоритет у задач, календаря, дел, судов и заметок; финансовые события остаются в CRM без лишних внешних сообщений.", [
                UserNotificationDomains.Task,
                UserNotificationDomains.Calendar,
                UserNotificationDomains.Note,
                UserNotificationDomains.Case,
                UserNotificationDomains.Court
            ]);
        }

        if (ContainsAny(normalized, "руковод", "директор", "управ"))
        {
            return BuildProfile("manager", "Руководитель", "Срочные задачи, согласования, финансы, дела, суды и системные предупреждения доступны во всех рабочих каналах.", [
                UserNotificationDomains.Task,
                UserNotificationDomains.Payment,
                UserNotificationDomains.Invoice,
                UserNotificationDomains.Act,
                UserNotificationDomains.Contract,
                UserNotificationDomains.Case,
                UserNotificationDomains.Court,
                UserNotificationDomains.System
            ]);
        }

        if (ContainsAny(normalized, "бухгалтер", "финанс", "касс", "банк"))
        {
            return BuildProfile("finance", "Бухгалтерия/финансы", "Финансовые уведомления остаются срочными, а непрофильные события не шумят во внешних каналах.", [
                UserNotificationDomains.Payment,
                UserNotificationDomains.Invoice,
                UserNotificationDomains.Act,
                UserNotificationDomains.Contract,
                UserNotificationDomains.Task
            ]);
        }

        return BuildProfile("employee", "Сотрудник", "Срочные задачи, календарь и заметки видны сразу; обычные изменения не перегружают email и Telegram.", [
            UserNotificationDomains.Task,
            UserNotificationDomains.Calendar,
            UserNotificationDomains.Note
        ]);
    }

    private static RoleNotificationPreferenceDefaultProfile BuildProfile(
        string key,
        string label,
        string description,
        IReadOnlyCollection<string> externalDomains)
    {
        var disabledChannels = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in UserNotificationTypes.All)
        {
            if (ShouldDisableExternalChannels(type, externalDomains))
            {
                disabledChannels[type.Key] = ExternalChannels;
            }
        }

        return new RoleNotificationPreferenceDefaultProfile(
            key,
            label,
            description,
            new UserNotificationPreferenceSnapshot([], disabledChannels));
    }

    private static bool ShouldDisableExternalChannels(
        UserNotificationTypeDefinition type,
        IReadOnlyCollection<string> externalDomains)
        => !NotificationPriorities.IsCritical(type.Priority)
           || type.Digestible
           || !externalDomains.Contains(type.Domain, StringComparer.OrdinalIgnoreCase);

    private static string NormalizeRoleName(string? roleName)
        => (roleName ?? string.Empty).Trim().ToLowerInvariant();

    private static bool ContainsAny(string value, params string[] fragments)
        => fragments.Any(fragment => value.Contains(fragment, StringComparison.Ordinal));
}

public static class RoleNotificationPreferenceSettings
{
    public const string SettingKey = "notifications.roleTypePreferences.v1";

    public static IReadOnlyDictionary<int, UserNotificationPreferenceSnapshot> Parse(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return new Dictionary<int, UserNotificationPreferenceSnapshot>();
        }

        try
        {
            var storage = JsonSerializer.Deserialize<RoleNotificationPreferenceStorage>(rawValue, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return Normalize(storage?.Roles);
        }
        catch (JsonException)
        {
            return new Dictionary<int, UserNotificationPreferenceSnapshot>();
        }
    }

    public static UserNotificationPreferenceSnapshot? ParseForRole(string? rawValue, int roleId)
        => Parse(rawValue).TryGetValue(roleId, out var preferences) ? preferences : null;

    public static string Serialize(IReadOnlyDictionary<int, UserNotificationPreferenceSnapshot> preferencesByRole)
    {
        var roles = preferencesByRole
            .Where(item => item.Key > 0)
            .OrderBy(item => item.Key)
            .ToDictionary(
                item => item.Key.ToString(CultureInfo.InvariantCulture),
                item => JsonSerializer.Deserialize<RoleNotificationPreferenceItem>(
                    UserNotificationPreferenceSettings.Serialize(item.Value),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new RoleNotificationPreferenceItem(),
                StringComparer.OrdinalIgnoreCase);

        return JsonSerializer.Serialize(new RoleNotificationPreferenceStorage { Roles = roles }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static IReadOnlyDictionary<int, UserNotificationPreferenceSnapshot> Normalize(Dictionary<string, RoleNotificationPreferenceItem>? values)
    {
        var result = new Dictionary<int, UserNotificationPreferenceSnapshot>();
        foreach (var (roleIdValue, preferences) in values ?? [])
        {
            if (!int.TryParse(roleIdValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var roleId) || roleId <= 0)
            {
                continue;
            }

            result[roleId] = UserNotificationPreferenceSettings.Parse(
                JsonSerializer.Serialize(preferences, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }

        return result;
    }

    private sealed class RoleNotificationPreferenceStorage
    {
        public Dictionary<string, RoleNotificationPreferenceItem> Roles { get; set; } = [];
    }

    private sealed class RoleNotificationPreferenceItem
    {
        public List<string> DisabledTypes { get; set; } = [];
        public Dictionary<string, List<string>> DisabledChannels { get; set; } = [];
    }
}

public sealed record EmailSendSettings(
    bool Enabled,
    string Host,
    int Port,
    string Username,
    string Password,
    string FromEmail,
    string FromName,
    bool EnableSsl);

public sealed record NotificationMessage(
    string Subject,
    string Body,
    string? HtmlBody = null,
    bool IsSensitive = false,
    string Purpose = NotificationPurposes.ManualTest);

public sealed record NotificationRecipient(
    int? UserId,
    string DisplayName,
    string? Email,
    long? TelegramChatId);

public sealed record TelegramSendResult(
    bool Success,
    string Status,
    string? Error = null);

public sealed record TelegramBotConnectionSettings(
    string ProxyUrl = "",
    string ProxyUsername = "",
    string ProxyPassword = "");

public sealed record TelegramBotProfilePhotoResult(
    byte[] Bytes,
    string ContentType,
    string FileName);

public interface ITelegramNotificationService
{
    Task<TelegramSendResult> SendMessageAsync(long chatId, string text, CancellationToken cancellationToken = default);
    Task<TelegramSendResult> SendMessageWithTokenAsync(
        long chatId,
        string text,
        string botToken,
        CancellationToken cancellationToken = default,
        TelegramBotConnectionSettings? connection = null);
    Task<TelegramNotificationSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default);
}

public interface ITelegramBotUpdateService
{
    Task<PayPlanner.Api.Models.Responses.TelegramWebhookSyncResultDto> SyncWebhookAsync(
        int senderId,
        HttpRequest request,
        CancellationToken cancellationToken = default);

    Task<PayPlanner.Api.Models.Responses.TelegramWebhookSyncResultDto> DeleteWebhookAsync(
        int senderId,
        CancellationToken cancellationToken = default);

    Task HandleWebhookAsync(
        int senderId,
        JsonDocument updateDocument,
        HttpRequest request,
        CancellationToken cancellationToken = default);

    Task PollEnabledSendersAsync(CancellationToken cancellationToken = default);

    Task<TelegramBotProfileSyncResultDto> SyncBotProfileAsync(
        int senderId,
        CancellationToken cancellationToken = default);

    Task<TelegramBotProfileSyncResultDto> ApplyBotProfileAsync(
        int senderId,
        PayPlanner.Api.Models.Requests.UpdateTelegramBotProfileRequest request,
        CancellationToken cancellationToken = default);

    Task<TelegramBotProfilePhotoResult?> GetBotProfilePhotoAsync(
        int senderId,
        CancellationToken cancellationToken = default);
}

public interface INotificationService
{
    Task<NotificationSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NotificationChannelDto>> GetChannelsAsync(CancellationToken cancellationToken = default);
    Task<NotificationSettingsDto> UpdateEmailSettingsAsync(
        PayPlanner.Api.Models.Requests.UpdateEmailNotificationSettingsRequest request,
        CancellationToken cancellationToken = default);
    Task<NotificationSettingsDto> UpdateTelegramSettingsAsync(
        PayPlanner.Api.Models.Requests.UpdateTelegramNotificationSettingsRequest request,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NotificationTemplateDto>> GetTemplatesAsync(CancellationToken cancellationToken = default);
    Task<NotificationTemplateDto> UpdateTemplateAsync(
        string key,
        PayPlanner.Api.Models.Requests.UpdateNotificationTemplateRequest request,
        CancellationToken cancellationToken = default);
    Task<NotificationTemplateDto> ResetTemplateAsync(string key, CancellationToken cancellationToken = default);
    Task<NotificationMessage> RenderTemplateAsync(
        string key,
        string fallbackSubject,
        string fallbackBody,
        IReadOnlyDictionary<string, string?> values,
        bool isSensitive = false,
        string purpose = NotificationPurposes.ManualTest,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NotificationDelivery>> SendToUserAsync(
        int userId,
        NotificationMessage message,
        IEnumerable<string>? channels = null,
        string? eventKey = null,
        CancellationToken cancellationToken = default,
        int? senderId = null);

    Task<IReadOnlyList<NotificationDelivery>> SendAsync(
        NotificationRecipient recipient,
        NotificationMessage message,
        IEnumerable<string>? channels = null,
        string? eventKey = null,
        CancellationToken cancellationToken = default,
        int? senderId = null);

    Task<IReadOnlyList<NotificationDelivery>> RetryDeliveryAsync(
        int deliveryId,
        CancellationToken cancellationToken = default);

    Task<bool> NotifyAsync(
        NotificationEventEnvelope notificationEvent,
        CancellationToken cancellationToken = default);

    Task<NotificationSettingsDto> UpsertSenderAsync(
        PayPlanner.Api.Models.Requests.UpsertNotificationSenderRequest request,
        int? senderId = null,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteSenderAsync(int senderId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NotificationDelivery>> NotifyPendingRegistrationAsync(User user, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NotificationDelivery>> NotifyRegistrationApprovedAsync(User user, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NotificationDelivery>> NotifyRegistrationRejectedAsync(User user, string? reason, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NotificationDelivery>> NotifyPasswordIssuedAsync(User user, string password, IEnumerable<string>? channels = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NotificationDelivery>> NotifyTaskAssignedAsync(TaskJournalItem task, string actorName, int actorUserId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NotificationDelivery>> NotifyTaskReminderAsync(TaskJournalItem task, string reminderKey, DateTime nowUtc, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NotificationDelivery>> NotifyTaskCompletedAsync(TaskJournalItem task, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NotificationDelivery>> NotifyTaskCancelledAsync(TaskJournalItem task, string actorName, int actorUserId, string action, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NotificationDelivery>> NotifyTaskScheduleConflictAsync(TaskJournalItem task, IReadOnlyList<TaskJournalItem> conflicts, string actorName, int actorUserId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NotificationDelivery>> NotifyTaskApprovalRequestedAsync(TaskJournalItem task, string actorName, int actorUserId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NotificationDelivery>> NotifyTaskApprovalDecisionAsync(TaskJournalItem task, TaskJournalParticipant approver, string decision, string actorName, int actorUserId, string? resolution = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NotificationDelivery>> NotifyTaskApprovalOverdueAsync(TaskJournalItem task, DateTime nowUtc, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NotificationDelivery>> NotifyTaskReassignedAsync(TaskJournalItem task, int? previousAssigneeUserId, string? previousAssigneeName, IReadOnlyList<string> changeLabels, string actorName, int actorUserId, string? comment = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NotificationDelivery>> NotifyTaskChangedAsync(TaskJournalItem task, IReadOnlyList<string> changeLabels, string actorName, int actorUserId, string? comment = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NotificationDelivery>> NotifyTaskCommentAsync(TaskJournalItem task, TaskJournalComment comment, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NotificationDelivery>> NotifyCaseInactivityControlTaskCreatedAsync(TaskJournalItem task, int daysWithoutActiveTask, CancellationToken cancellationToken = default);
    Task NotifyCalendarAccessChangedAsync(int roleId, IReadOnlyCollection<int> addedVisibleUserIds, IReadOnlyCollection<int> removedVisibleUserIds, string actorName, int actorUserId, CancellationToken cancellationToken = default);
    Task NotifyExternalCalendarSyncWarningAsync(string providerName, string operation, string summary, string? details = null, CancellationToken cancellationToken = default);
    Task NotifyStickyNoteMentionAsync(int noteId, int boardId, string boardTitle, string noteText, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default);
    Task NotifyStickyNoteAssignedAsync(int noteId, int boardId, string boardTitle, string noteText, int assignedUserId, string actorName, int actorUserId, DateTime? reminderAt, CancellationToken cancellationToken = default);
    Task NotifyStickyNoteReminderAsync(StickyNote note, CancellationToken cancellationToken = default);
    Task NotifyStickyNoteImportantChangeAsync(int noteId, int boardId, string boardTitle, string noteText, string action, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, int noteVersion, CancellationToken cancellationToken = default);
    Task NotifyPaymentAssignedAsync(Payment payment, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default);
    Task NotifyPaymentChangedAsync(Payment payment, IReadOnlyList<string> changeLabels, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default);
    Task NotifyInvoiceCreatedAsync(Payment invoicePayment, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default);
    Task NotifyInvoiceChangedAsync(Payment invoicePayment, IReadOnlyList<string> changeLabels, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default);
    Task NotifyActCreatedAsync(Act act, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default);
    Task NotifyActChangedAsync(Act act, IReadOnlyList<string> changeLabels, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default);
    Task NotifyContractCreatedAsync(Contract contract, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default);
    Task NotifyContractChangedAsync(Contract contract, IReadOnlyList<string> changeLabels, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default);
    Task NotifyContractExpiringAsync(Contract contract, int daysLeft, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default);
    Task NotifyCaseCourtEventAsync(ClientCase clientCase, ClientCaseCourtEvent courtEvent, string action, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default);
    Task NotifyCaseProceduralDeadlineAsync(ClientCase clientCase, ClientCaseProceduralDeadline deadline, string action, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default);
    Task NotifyCaseDocumentAsync(ClientCase clientCase, ClientCaseDocument document, string action, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default);
    Task NotifyCaseAccessRequestedAsync(ClientCase clientCase, string requesterName, int requesterUserId, string? reason, DateTime requestedAt, IReadOnlyCollection<int> managerUserIds, CancellationToken cancellationToken = default);
    Task NotifyCaseAccessResolvedAsync(ClientCase clientCase, bool granted, string actorName, int actorUserId, int requestedByUserId, string requestedByUserName, string? requestReason, string? comment, DateTime resolvedAt, CancellationToken cancellationToken = default);
    Task NotifyCourtSyncFailedAsync(ClientCase clientCase, string providerName, string summary, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default);
}
