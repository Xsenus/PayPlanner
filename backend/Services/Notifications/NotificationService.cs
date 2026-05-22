using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services.Notifications;

public sealed class NotificationService : INotificationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly CultureInfo RuCulture = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly IReadOnlyList<NotificationPurposeDefinition> PurposeDefinitions =
    [
        new(NotificationPurposes.TelegramAuth, "Вход через Telegram", "Telegram-бот для авторизации, привязки профиля и Login Widget."),
        new(NotificationPurposes.RegistrationUser, "Регистрация пользователя", "Сообщения пользователю о заявке, одобрении или отклонении регистрации."),
        new(NotificationPurposes.RegistrationAdmin, "Новая регистрация для админов", "Уведомления администраторам о новой заявке на регистрацию."),
        new(NotificationPurposes.PasswordIssue, "Выдача пароля", "Служебные сообщения с новым временным паролем сотрудника."),
        new(NotificationPurposes.TaskReminder, "Напоминания по задачам", "Плановые уведомления из журнала задач."),
        new(NotificationPurposes.ManualTest, "Ручные и тестовые сообщения", "Проверочные отправки из настроек уведомлений.")
    ];

    private const string EmailEnabledKey = "notifications.email.enabled";
    private const string EmailContractTerminationClientEnabledKey = "notifications.email.contractTerminationClientEnabled";
    private const string EmailSmtpHostKey = "notifications.email.smtpHost";
    private const string EmailSmtpPortKey = "notifications.email.smtpPort";
    private const string EmailSmtpUsernameKey = "notifications.email.smtpUsername";
    private const string EmailSmtpPasswordKey = "notifications.email.smtpPassword";
    private const string EmailFromEmailKey = "notifications.email.fromEmail";
    private const string EmailFromNameKey = "notifications.email.fromName";
    private const string EmailEnableSslKey = "notifications.email.enableSsl";

    private const string TelegramEnabledKey = "notifications.telegram.enabled";
    private const string TelegramBotTokenKey = "notifications.telegram.botToken";
    private const string RegistrationNotifyAdminsKey = "notifications.registration.notifyAdmins";

    private readonly PaymentContext _context;
    private readonly IConfiguration _configuration;
    private readonly IEmailService _emailService;
    private readonly ITelegramNotificationService _telegramService;
    private readonly ILogger<NotificationService> _logger;

    private sealed record NotificationSenderTarget(
        int? Id,
        string Channel,
        string Name,
        bool Ready,
        EmailSendSettings? Email,
        string? TelegramBotToken,
        TelegramBotConnectionSettings? TelegramConnection,
        string? NotReadyReason);

    private sealed class EmailSenderConfig
    {
        public string SmtpHost { get; set; } = string.Empty;
        public int SmtpPort { get; set; } = 587;
        public string SmtpUsername { get; set; } = string.Empty;
        public string SmtpPassword { get; set; } = string.Empty;
        public string FromEmail { get; set; } = string.Empty;
        public string FromName { get; set; } = "PayPlanner";
        public bool EnableSsl { get; set; } = true;
    }

    public NotificationService(
        PaymentContext context,
        IConfiguration configuration,
        IEmailService emailService,
        ITelegramNotificationService telegramService,
        ILogger<NotificationService> logger)
    {
        _context = context;
        _configuration = configuration;
        _emailService = emailService;
        _telegramService = telegramService;
        _logger = logger;
    }

    public async Task<NotificationSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await LoadSettingsAsync(cancellationToken);
        var email = BuildEmailSettings(settings);
        var telegram = await _telegramService.GetSettingsAsync(cancellationToken);
        var senders = await _context.NotificationSenders
            .AsNoTracking()
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        var subscriptionCounts = await _context.NotificationBotSubscriptions
            .AsNoTracking()
            .Where(item => item.IsActive)
            .GroupBy(item => item.NotificationSenderId)
            .Select(group => new { SenderId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.SenderId, item => item.Count, cancellationToken);

        return new NotificationSettingsDto
        {
            Email = email,
            Telegram = telegram,
            Senders = senders.Select(sender => MapSender(sender, subscriptionCounts.GetValueOrDefault(sender.Id))).ToList(),
            Purposes = PurposeDefinitions.Select(item => new NotificationPurposeDto
            {
                Key = item.Key,
                Label = item.Label,
                Description = item.Description
            }).ToList()
        };
    }

    public async Task<NotificationSettingsDto> UpdateEmailSettingsAsync(
        UpdateEmailNotificationSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await UpsertSettingAsync(EmailEnabledKey, request.Enabled ? "true" : "false", now, cancellationToken);
        await UpsertSettingAsync(EmailContractTerminationClientEnabledKey, request.ContractTerminationClientEmailsEnabled ? "true" : "false", now, cancellationToken);
        await UpsertSettingAsync(EmailSmtpHostKey, NormalizeOptional(request.SmtpHost) ?? string.Empty, now, cancellationToken);
        await UpsertSettingAsync(EmailSmtpPortKey, Math.Clamp(request.SmtpPort ?? 587, 1, 65535).ToString(CultureInfo.InvariantCulture), now, cancellationToken);
        await UpsertSettingAsync(EmailSmtpUsernameKey, NormalizeOptional(request.SmtpUsername) ?? string.Empty, now, cancellationToken);
        await UpsertSettingAsync(EmailFromEmailKey, NormalizeOptional(request.FromEmail) ?? string.Empty, now, cancellationToken);
        await UpsertSettingAsync(EmailFromNameKey, NormalizeOptional(request.FromName) ?? "PayPlanner", now, cancellationToken);
        await UpsertSettingAsync(EmailEnableSslKey, request.EnableSsl ? "true" : "false", now, cancellationToken);

        if (!request.KeepExistingPassword || !string.IsNullOrWhiteSpace(request.SmtpPassword))
        {
            await UpsertSettingAsync(EmailSmtpPasswordKey, NormalizeOptional(request.SmtpPassword) ?? string.Empty, now, cancellationToken);
        }

        await _context.SaveChangesAsync(cancellationToken);
        return await GetSettingsAsync(cancellationToken);
    }

    public async Task<NotificationSettingsDto> UpdateTelegramSettingsAsync(
        UpdateTelegramNotificationSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await UpsertSettingAsync(TelegramEnabledKey, request.Enabled ? "true" : "false", now, cancellationToken);

        if (!request.KeepExistingToken || !string.IsNullOrWhiteSpace(request.BotToken))
        {
            await UpsertSettingAsync(TelegramBotTokenKey, NormalizeOptional(request.BotToken) ?? string.Empty, now, cancellationToken);
        }

        await _context.SaveChangesAsync(cancellationToken);
        return await GetSettingsAsync(cancellationToken);
    }

    public async Task<NotificationSettingsDto> UpsertSenderAsync(
        UpsertNotificationSenderRequest request,
        int? senderId = null,
        CancellationToken cancellationToken = default)
    {
        var channel = NormalizeChannel(request.Channel);
        if (channel is not (NotificationChannels.Email or NotificationChannels.Telegram))
        {
            throw new InvalidOperationException("NotificationSenderChannelInvalid");
        }

        var sender = senderId.HasValue
            ? await _context.NotificationSenders.FirstOrDefaultAsync(item => item.Id == senderId.Value, cancellationToken)
            : null;
        if (senderId.HasValue && sender is null)
        {
            throw new InvalidOperationException("NotificationSenderNotFound");
        }

        var now = DateTime.UtcNow;
        sender ??= new NotificationSender
        {
            CreatedAt = now,
            SortOrder = await ResolveNextSenderOrderAsync(channel, cancellationToken)
        };

        if (sender.Id == 0)
        {
            _context.NotificationSenders.Add(sender);
        }

        sender.Channel = channel;
        sender.Name = string.IsNullOrWhiteSpace(request.Name)
            ? DefaultSenderName(channel)
            : request.Name.Trim();
        sender.IsEnabled = request.IsEnabled;
        sender.PurposesJson = JsonSerializer.Serialize(NormalizePurposes(request.Purposes), JsonOptions);
        sender.SortOrder = request.SortOrder;
        sender.UpdatedAt = now;
        sender.ConfigJson = channel == NotificationChannels.Email
            ? JsonSerializer.Serialize(BuildEmailSenderConfig(request.Email, sender.ConfigJson), JsonOptions)
            : JsonSerializer.Serialize(BuildTelegramSenderConfig(request.Telegram, sender.ConfigJson), JsonOptions);

        await _context.SaveChangesAsync(cancellationToken);
        return await GetSettingsAsync(cancellationToken);
    }

    public async Task<bool> DeleteSenderAsync(int senderId, CancellationToken cancellationToken = default)
    {
        var sender = await _context.NotificationSenders.FirstOrDefaultAsync(item => item.Id == senderId, cancellationToken);
        if (sender is null)
        {
            return false;
        }

        _context.NotificationSenders.Remove(sender);
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<NotificationDelivery>> SendToUserAsync(
        int userId,
        NotificationMessage message,
        IEnumerable<string>? channels = null,
        string? eventKey = null,
        CancellationToken cancellationToken = default,
        int? senderId = null)
    {
        var user = await _context.Users
            .AsNoTracking()
            .Include(item => item.ExternalIdentities)
            .FirstOrDefaultAsync(item => item.Id == userId, cancellationToken);
        if (user is null)
        {
            return Array.Empty<NotificationDelivery>();
        }

        var recipient = BuildRecipient(user);
        return await SendAsync(recipient, message, channels, eventKey, cancellationToken, senderId);
    }

    public async Task<IReadOnlyList<NotificationDelivery>> NotifyPendingRegistrationAsync(User user, CancellationToken cancellationToken = default)
    {
        var deliveries = new List<NotificationDelivery>();
        var recipient = await ResolveRecipientAsync(user.Id, user, cancellationToken);
        deliveries.AddRange(await SendAsync(
            recipient,
            new NotificationMessage(
                "Регистрация ожидает подтверждения",
                $"Здравствуйте, {DisplayName(user)}!\n\nВаша заявка на регистрацию получена и ожидает проверки администратором.",
                Purpose: NotificationPurposes.RegistrationUser),
            [NotificationChannels.Email, NotificationChannels.Telegram],
            $"registration:{user.Id}:pending",
            cancellationToken));

        if (await ShouldNotifyAdminsOnRegistrationAsync(cancellationToken))
        {
            var admins = await _context.Users
                .AsNoTracking()
                .Include(item => item.Role)
                .Include(item => item.ExternalIdentities)
                .Where(item => item.IsActive && item.IsApproved && item.Role != null && item.Role.Name == "admin")
                .ToListAsync(cancellationToken);

            foreach (var admin in admins)
            {
                deliveries.AddRange(await SendAsync(
                    BuildRecipient(admin),
                    new NotificationMessage(
                        "Новая регистрация в CRM",
                        $"В системе зарегистрировался новый пользователь.\n\nИмя: {DisplayName(user)}\nEmail: {user.Email}",
                        Purpose: NotificationPurposes.RegistrationAdmin),
                    [NotificationChannels.Email, NotificationChannels.Telegram],
                    $"registration:{user.Id}:admin:{admin.Id}",
                    cancellationToken));
            }
        }

        return deliveries;
    }

    public async Task<IReadOnlyList<NotificationDelivery>> NotifyRegistrationApprovedAsync(User user, CancellationToken cancellationToken = default)
    {
        var recipient = await ResolveRecipientAsync(user.Id, user, cancellationToken);
        return await SendAsync(
            recipient,
            new NotificationMessage(
                "Регистрация одобрена",
                $"Здравствуйте, {DisplayName(user)}!\n\nДоступ к PayPlanner одобрен. Теперь можно войти в систему.",
                Purpose: NotificationPurposes.RegistrationUser),
            [NotificationChannels.Email, NotificationChannels.Telegram],
            $"registration:{user.Id}:approved:{DateTime.UtcNow:yyyyMMddHHmmss}",
            cancellationToken);
    }

    public async Task<IReadOnlyList<NotificationDelivery>> NotifyRegistrationRejectedAsync(User user, string? reason, CancellationToken cancellationToken = default)
    {
        var reasonText = string.IsNullOrWhiteSpace(reason) ? "Причина не указана." : reason.Trim();
        var recipient = await ResolveRecipientAsync(user.Id, user, cancellationToken);
        return await SendAsync(
            recipient,
            new NotificationMessage(
                "Регистрация отклонена",
                $"Здравствуйте, {DisplayName(user)}!\n\nЗаявка на регистрацию отклонена.\nПричина: {reasonText}",
                Purpose: NotificationPurposes.RegistrationUser),
            [NotificationChannels.Email, NotificationChannels.Telegram],
            $"registration:{user.Id}:rejected:{DateTime.UtcNow:yyyyMMddHHmmss}",
            cancellationToken);
    }

    public async Task<IReadOnlyList<NotificationDelivery>> NotifyPasswordIssuedAsync(
        User user,
        string password,
        IEnumerable<string>? channels = null,
        CancellationToken cancellationToken = default)
    {
        var recipient = await ResolveRecipientAsync(user.Id, user, cancellationToken);
        return await SendAsync(
            recipient,
            new NotificationMessage(
                "Выдан новый пароль",
                $"Здравствуйте, {DisplayName(user)}!\n\nДля вашей учетной записи выдан новый пароль:\n{password}\n\nПосле входа рекомендуем сменить его в профиле.",
                IsSensitive: true,
                Purpose: NotificationPurposes.PasswordIssue),
            channels ?? [NotificationChannels.Email, NotificationChannels.Telegram],
            $"password-issued:{user.Id}:{DateTime.UtcNow:yyyyMMddHHmmss}",
            cancellationToken);
    }

    public async Task<IReadOnlyList<NotificationDelivery>> NotifyTaskReminderAsync(
        TaskJournalItem task,
        string reminderKey,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (!task.AssigneeUserId.HasValue)
        {
            return Array.Empty<NotificationDelivery>();
        }

        var inAppEnabled = HasTaskChannel(task, NotificationChannels.InApp);
        var channels = ResolveTaskMessageChannels(task);

        var eventKey = reminderKey == "dailyOverdue"
            ? $"task:{task.Id}:reminder:{reminderKey}:{nowUtc:yyyyMMdd}"
            : $"task:{task.Id}:reminder:{reminderKey}";

        var message = new NotificationMessage(
            BuildTaskReminderSubject(task, reminderKey, nowUtc),
            BuildTaskReminderBody(task, reminderKey, nowUtc),
            Purpose: NotificationPurposes.TaskReminder);

        if (inAppEnabled)
        {
            await CreateUserNotificationAsync(
                task.AssigneeUserId.Value,
                UserNotificationTypes.TaskReminder,
                eventKey,
                message.Subject,
                message.Body,
                IsReminderOverdue(task, reminderKey, nowUtc) ? "rose" : "amber",
                task,
                new { reminderKey, dueAt = task.DueAt },
                cancellationToken);
        }

        if (channels.Count == 0)
        {
            return Array.Empty<NotificationDelivery>();
        }

        return await SendToUserAsync(task.AssigneeUserId.Value, message, channels, eventKey, cancellationToken);
    }

    public async Task<IReadOnlyList<NotificationDelivery>> NotifyTaskAssignedAsync(
        TaskJournalItem task,
        string actorName,
        int actorUserId,
        CancellationToken cancellationToken = default)
    {
        var channels = ResolveTaskMessageChannels(task);
        var recipients = ResolveTaskNotificationUserIds(task, actorUserId);
        if (recipients.Count == 0)
        {
            return Array.Empty<NotificationDelivery>();
        }

        var message = new NotificationMessage(
            $"Новая задача: {task.Title}",
            BuildTaskAssignedBody(task, actorName),
            Purpose: NotificationPurposes.TaskReminder);
        var eventKey = $"task:{task.Id}:assigned";
        var deliveries = new List<NotificationDelivery>();
        foreach (var userId in recipients)
        {
            if (HasTaskChannel(task, NotificationChannels.InApp))
            {
                await CreateUserNotificationAsync(
                    userId,
                    UserNotificationTypes.TaskAssigned,
                    eventKey,
                    message.Subject,
                    message.Body,
                    "blue",
                    task,
                    new { actorUserId, actorName, task.AssigneeUserId, task.AssigneeName },
                    cancellationToken);
            }

            if (channels.Count > 0)
            {
                deliveries.AddRange(await SendToUserAsync(userId, message, channels, eventKey, cancellationToken));
            }
        }

        return deliveries;
    }

    public async Task<IReadOnlyList<NotificationDelivery>> NotifyTaskCompletedAsync(
        TaskJournalItem task,
        CancellationToken cancellationToken = default)
    {
        if (!task.AuthorUserId.HasValue)
        {
            return Array.Empty<NotificationDelivery>();
        }

        var message = new NotificationMessage(
            $"Задача выполнена: {task.Title}",
            BuildTaskCompletedBody(task),
            Purpose: NotificationPurposes.TaskReminder);

        if (HasTaskChannel(task, NotificationChannels.InApp))
        {
            await CreateUserNotificationAsync(
                task.AuthorUserId.Value,
                UserNotificationTypes.TaskCompleted,
                $"task:{task.Id}:completed",
                message.Subject,
                message.Body,
                "blue",
                task,
                new { status = task.Status },
                cancellationToken);
        }

        return await SendToUserAsync(
            task.AuthorUserId.Value,
            message,
            [NotificationChannels.Email, NotificationChannels.Telegram],
            $"task:{task.Id}:completed",
            cancellationToken);
    }

    public async Task<IReadOnlyList<NotificationDelivery>> NotifyCaseInactivityControlTaskCreatedAsync(
        TaskJournalItem task,
        int daysWithoutActiveTask,
        CancellationToken cancellationToken = default)
    {
        if (!task.AssigneeUserId.HasValue)
        {
            return Array.Empty<NotificationDelivery>();
        }

        var channels = ResolveTaskMessageChannels(task);
        var recipientUserIds = new[] { task.AssigneeUserId.Value }
            .Concat(task.Participants
                .Where(participant =>
                    participant.UserId.HasValue &&
                    string.Equals(participant.Role, "observer", StringComparison.OrdinalIgnoreCase))
                .Select(participant => participant.UserId!.Value))
            .Distinct()
            .ToList();
        var eventKey = $"case:{task.ClientCaseId ?? 0}:inactivity-control:{task.Id}";
        var message = new NotificationMessage(
            $"Автоконтроль дела: {task.ObjectName}",
            BuildCaseInactivityControlBody(task, daysWithoutActiveTask),
            Purpose: NotificationPurposes.TaskReminder);

        if (HasTaskChannel(task, NotificationChannels.InApp))
        {
            foreach (var recipientUserId in recipientUserIds)
            {
                await CreateUserNotificationAsync(
                    recipientUserId,
                    UserNotificationTypes.CaseInactivityControl,
                    eventKey,
                    message.Subject,
                    message.Body,
                    "amber",
                    task,
                    new
                    {
                        task.ClientCaseId,
                        daysWithoutActiveTask,
                        task.AssigneeUserId,
                        isEscalationObserver = recipientUserId != task.AssigneeUserId.Value
                    },
                    cancellationToken);
            }
        }

        if (channels.Count == 0)
        {
            return Array.Empty<NotificationDelivery>();
        }

        var deliveries = new List<NotificationDelivery>();
        foreach (var recipientUserId in recipientUserIds)
        {
            deliveries.AddRange(await SendToUserAsync(recipientUserId, message, channels, eventKey, cancellationToken));
        }

        return deliveries;
    }

    public async Task<IReadOnlyList<NotificationDelivery>> NotifyTaskScheduleConflictAsync(
        TaskJournalItem task,
        IReadOnlyList<TaskJournalItem> conflicts,
        string actorName,
        int actorUserId,
        CancellationToken cancellationToken = default)
    {
        var effectiveConflicts = conflicts
            .Where(item => item.Id != task.Id)
            .OrderBy(item => item.DueAt)
            .ThenBy(item => item.Id)
            .Take(5)
            .ToList();
        if (effectiveConflicts.Count == 0)
        {
            return Array.Empty<NotificationDelivery>();
        }

        var recipients = ResolveTaskNotificationUserIds(task, actorUserId);
        if (recipients.Count == 0)
        {
            return Array.Empty<NotificationDelivery>();
        }

        var channels = ResolveTaskMessageChannels(task);
        var message = new NotificationMessage(
            $"Конфликт расписания: {task.Title}",
            BuildTaskScheduleConflictBody(task, effectiveConflicts, actorName),
            Purpose: NotificationPurposes.TaskReminder);
        var conflictKey = string.Join('-', effectiveConflicts.Select(item => item.Id).OrderBy(id => id));
        var eventKey = $"task:{task.Id}:schedule-conflict:{task.DueAt:yyyyMMddHHmm}:{task.PlannedDurationMinutes}:{conflictKey}";
        var deliveries = new List<NotificationDelivery>();

        foreach (var userId in recipients)
        {
            if (HasTaskChannel(task, NotificationChannels.InApp))
            {
                await CreateUserNotificationAsync(
                    userId,
                    UserNotificationTypes.TaskScheduleConflict,
                    eventKey,
                    message.Subject,
                    message.Body,
                    "rose",
                    task,
                    new
                    {
                        actorUserId,
                        actorName,
                        conflictTaskIds = effectiveConflicts.Select(item => item.Id).ToArray()
                    },
                    cancellationToken);
            }

            if (channels.Count > 0)
            {
                deliveries.AddRange(await SendToUserAsync(userId, message, channels, eventKey, cancellationToken));
            }
        }

        return deliveries;
    }

    public async Task<IReadOnlyList<NotificationDelivery>> NotifyTaskApprovalRequestedAsync(
        TaskJournalItem task,
        string actorName,
        int actorUserId,
        CancellationToken cancellationToken = default)
    {
        var recipients = ResolveApprovalRequestRecipientUserIds(task, actorUserId);
        if (recipients.Count == 0)
        {
            return Array.Empty<NotificationDelivery>();
        }

        var channels = ResolveTaskMessageChannels(task);
        var currentApprover = GetCurrentApprover(task);
        var message = new NotificationMessage(
            $"Задача ожидает согласования: {task.Title}",
            BuildTaskApprovalRequestedBody(task, actorName, currentApprover),
            Purpose: NotificationPurposes.TaskReminder);
        var eventKey = $"task:{task.Id}:approval-request:{currentApprover?.Id ?? 0}:{DateTime.UtcNow:yyyyMMddHHmmssfff}";
        var deliveries = new List<NotificationDelivery>();
        foreach (var userId in recipients)
        {
            if (HasTaskChannel(task, NotificationChannels.InApp))
            {
                await CreateUserNotificationAsync(
                    userId,
                    UserNotificationTypes.TaskApprovalRequested,
                    eventKey,
                    message.Subject,
                    message.Body,
                    "violet",
                    task,
                    new { actorUserId, actorName, currentApproverId = currentApprover?.Id },
                    cancellationToken);
            }

            if (channels.Count > 0)
            {
                deliveries.AddRange(await SendToUserAsync(userId, message, channels, eventKey, cancellationToken));
            }
        }

        return deliveries;
    }

    public async Task<IReadOnlyList<NotificationDelivery>> NotifyTaskApprovalDecisionAsync(
        TaskJournalItem task,
        TaskJournalParticipant approver,
        string decision,
        string actorName,
        int actorUserId,
        string? resolution = null,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(decision, "approved", StringComparison.OrdinalIgnoreCase)
            && IsTaskWaitingForNextApprover(task))
        {
            return await NotifyTaskApprovalRequestedAsync(task, actorName, actorUserId, cancellationToken);
        }

        var isRevision = string.Equals(decision, "revisionRequested", StringComparison.OrdinalIgnoreCase);
        var type = isRevision ? UserNotificationTypes.TaskRevisionRequested : UserNotificationTypes.TaskApproved;
        var tone = isRevision ? "orange" : "emerald";
        var subject = isRevision
            ? $"Задача возвращена на доработку: {task.Title}"
            : $"Задача согласована: {task.Title}";
        var recipients = ResolveApprovalDecisionRecipientUserIds(task, actorUserId);
        if (recipients.Count == 0)
        {
            return Array.Empty<NotificationDelivery>();
        }

        var channels = ResolveTaskMessageChannels(task);
        var message = new NotificationMessage(
            subject,
            BuildTaskApprovalDecisionBody(task, approver, decision, actorName, resolution),
            Purpose: NotificationPurposes.TaskReminder);
        var eventKey = $"task:{task.Id}:approval-decision:{approver.Id}:{decision}:{DateTime.UtcNow:yyyyMMddHHmmssfff}";
        var deliveries = new List<NotificationDelivery>();
        foreach (var userId in recipients)
        {
            if (HasTaskChannel(task, NotificationChannels.InApp))
            {
                await CreateUserNotificationAsync(
                    userId,
                    type,
                    eventKey,
                    message.Subject,
                    message.Body,
                    tone,
                    task,
                    new { actorUserId, actorName, approverId = approver.Id, decision, resolution },
                    cancellationToken);
            }

            if (channels.Count > 0)
            {
                deliveries.AddRange(await SendToUserAsync(userId, message, channels, eventKey, cancellationToken));
            }
        }

        return deliveries;
    }

    public async Task<IReadOnlyList<NotificationDelivery>> NotifyTaskChangedAsync(
        TaskJournalItem task,
        IReadOnlyList<string> changeLabels,
        string actorName,
        int actorUserId,
        string? comment = null,
        CancellationToken cancellationToken = default)
    {
        var channels = ResolveTaskMessageChannels(task);
        var recipients = ResolveTaskNotificationUserIds(task, actorUserId);
        if (recipients.Count == 0)
        {
            return Array.Empty<NotificationDelivery>();
        }

        var message = new NotificationMessage(
            $"Изменения по задаче: {task.Title}",
            BuildTaskChangedBody(task, changeLabels, actorName, comment),
            Purpose: NotificationPurposes.TaskReminder);
        var eventKey = $"task:{task.Id}:changed:{DateTime.UtcNow:yyyyMMddHHmmssfff}:{actorUserId}";
        var deliveries = new List<NotificationDelivery>();
        foreach (var userId in recipients)
        {
            if (HasTaskChannel(task, NotificationChannels.InApp))
            {
                await CreateUserNotificationAsync(
                    userId,
                    UserNotificationTypes.TaskChanged,
                    eventKey,
                    message.Subject,
                    message.Body,
                    "blue",
                    task,
                    new { actorUserId, actorName, changeLabels },
                    cancellationToken);
            }

            if (channels.Count > 0)
            {
                deliveries.AddRange(await SendToUserAsync(userId, message, channels, eventKey, cancellationToken));
            }
        }

        return deliveries;
    }

    public async Task<IReadOnlyList<NotificationDelivery>> NotifyTaskCommentAsync(
        TaskJournalItem task,
        TaskJournalComment comment,
        CancellationToken cancellationToken = default)
    {
        var channels = ResolveTaskMessageChannels(task);
        var actorUserId = comment.AuthorUserId ?? 0;
        var recipients = ResolveTaskNotificationUserIds(task, actorUserId);
        if (recipients.Count == 0)
        {
            return Array.Empty<NotificationDelivery>();
        }

        var message = new NotificationMessage(
            $"Комментарий по задаче: {task.Title}",
            BuildTaskCommentBody(task, comment),
            Purpose: NotificationPurposes.TaskReminder);
        var eventKey = $"task:{task.Id}:comment:{comment.Id}";
        var deliveries = new List<NotificationDelivery>();
        foreach (var userId in recipients)
        {
            if (HasTaskChannel(task, NotificationChannels.InApp))
            {
                await CreateUserNotificationAsync(
                    userId,
                    UserNotificationTypes.TaskComment,
                    eventKey,
                    message.Subject,
                    message.Body,
                    "emerald",
                    task,
                    new { commentId = comment.Id, comment.AuthorUserId, comment.AuthorName },
                    cancellationToken);
            }

            if (channels.Count > 0)
            {
                deliveries.AddRange(await SendToUserAsync(userId, message, channels, eventKey, cancellationToken));
            }
        }

        return deliveries;
    }

    public async Task<IReadOnlyList<NotificationDelivery>> SendAsync(
        NotificationRecipient recipient,
        NotificationMessage message,
        IEnumerable<string>? channels,
        string? eventKey,
        CancellationToken cancellationToken,
        int? senderId = null)
    {
        var deliveries = new List<NotificationDelivery>();
        foreach (var channel in NormalizeChannels(channels))
        {
            var targets = await ResolveSenderTargetsAsync(channel, message.Purpose, senderId, cancellationToken);
            foreach (var target in targets)
            {
                if (!string.IsNullOrWhiteSpace(eventKey)
                    && await HasCompletedDeliveryAsync(eventKey, channel, recipient, target.Id, cancellationToken))
                {
                    continue;
                }

                var delivery = new NotificationDelivery
                {
                    EventKey = eventKey ?? $"manual:{Guid.NewGuid():N}",
                    Channel = channel,
                    NotificationSenderId = target.Id,
                    SenderName = target.Name,
                    RecipientUserId = recipient.UserId,
                    RecipientAddress = ResolveRecipientAddress(recipient, channel),
                    Subject = message.Subject,
                    BodyPreview = message.IsSensitive ? "Содержимое скрыто: служебное уведомление." : Truncate(message.Body, 1000),
                    Status = NotificationDeliveryStatus.Pending,
                    CreatedAt = DateTime.UtcNow,
                };
                _context.NotificationDeliveries.Add(delivery);
                await _context.SaveChangesAsync(cancellationToken);

                try
                {
                    await SendDeliveryAsync(delivery, target, recipient, message, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Notification delivery {DeliveryId} failed.", delivery.Id);
                    delivery.Status = NotificationDeliveryStatus.Failed;
                    delivery.Error = ex.Message;
                }

                await _context.SaveChangesAsync(cancellationToken);
                deliveries.Add(delivery);
            }
        }

        return deliveries;
    }

    public async Task<IReadOnlyList<NotificationDelivery>> RetryDeliveryAsync(
        int deliveryId,
        CancellationToken cancellationToken = default)
    {
        var original = await _context.NotificationDeliveries
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == deliveryId, cancellationToken);
        if (original is null)
        {
            throw new InvalidOperationException("NotificationDeliveryNotFound");
        }

        if (string.Equals(original.Status, NotificationDeliveryStatus.Sent, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("NotificationDeliveryAlreadySent");
        }

        if (string.Equals(original.Status, NotificationDeliveryStatus.Pending, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("NotificationDeliveryPending");
        }

        var channel = NormalizeChannel(original.Channel);
        if (channel is not (NotificationChannels.Email or NotificationChannels.Telegram))
        {
            throw new InvalidOperationException("NotificationDeliveryChannelInvalid");
        }

        var message = new NotificationMessage(
            string.IsNullOrWhiteSpace(original.Subject) ? "Повтор уведомления PayPlanner" : original.Subject,
            string.IsNullOrWhiteSpace(original.BodyPreview) ? "Повторная отправка уведомления PayPlanner." : original.BodyPreview);
        var eventKey = $"{original.EventKey}:retry:{DateTime.UtcNow:yyyyMMddHHmmssfff}:{Guid.NewGuid():N}";

        if (original.RecipientUserId.HasValue)
        {
            return await SendToUserAsync(
                original.RecipientUserId.Value,
                message,
                [channel],
                eventKey,
                cancellationToken,
                original.NotificationSenderId);
        }

        var recipient = BuildRetryRecipient(original, channel);
        if (recipient is null)
        {
            throw new InvalidOperationException("NotificationDeliveryRecipientMissing");
        }

        return await SendAsync(
            recipient,
            message,
            [channel],
            eventKey,
            cancellationToken,
            original.NotificationSenderId);
    }

    private async Task SendDeliveryAsync(
        NotificationDelivery delivery,
        NotificationSenderTarget target,
        NotificationRecipient recipient,
        NotificationMessage message,
        CancellationToken cancellationToken)
    {
        if (!target.Ready)
        {
            delivery.Status = NotificationDeliveryStatus.Skipped;
            delivery.Error = target.NotReadyReason ?? "Отправитель уведомления не готов к отправке.";
            return;
        }

        if (delivery.Channel == NotificationChannels.Email)
        {
            if (string.IsNullOrWhiteSpace(recipient.Email))
            {
                delivery.Status = NotificationDeliveryStatus.Skipped;
                delivery.Error = "У пользователя не указан email.";
                return;
            }

            await _emailService.SendEmailAsync(
                recipient.Email,
                message.Subject,
                message.HtmlBody ?? message.Body,
                isHtml: !string.IsNullOrWhiteSpace(message.HtmlBody),
                cancellationToken,
                target.Email);
            delivery.Status = NotificationDeliveryStatus.Sent;
            delivery.SentAt = DateTime.UtcNow;
            return;
        }

        if (delivery.Channel == NotificationChannels.Telegram)
        {
            var chatId = await ResolveTelegramChatIdAsync(recipient, target.Id, cancellationToken);
            if (!chatId.HasValue)
            {
                delivery.Status = NotificationDeliveryStatus.Skipped;
                delivery.Error = target.Id.HasValue
                    ? "Пользователь не запускал этого Telegram-бота или не привязан к нему."
                    : "Пользователь не привязал Telegram bot chat id.";
                return;
            }

            var result = string.IsNullOrWhiteSpace(target.TelegramBotToken)
                ? await _telegramService.SendMessageAsync(chatId.Value, message.Body, cancellationToken)
                : await _telegramService.SendMessageWithTokenAsync(
                    chatId.Value,
                    message.Body,
                    target.TelegramBotToken,
                    cancellationToken,
                    target.TelegramConnection);
            delivery.Status = result.Status;
            delivery.Error = result.Error;
            delivery.SentAt = result.Success ? DateTime.UtcNow : null;
            return;
        }

        delivery.Status = NotificationDeliveryStatus.Skipped;
        delivery.Error = $"Канал '{delivery.Channel}' не поддерживается.";
    }

    private static NotificationRecipient? BuildRetryRecipient(NotificationDelivery delivery, string channel)
    {
        var address = delivery.RecipientAddress?.Trim();
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        if (channel == NotificationChannels.Email)
        {
            return new NotificationRecipient(null, address, address, null);
        }

        return long.TryParse(address, NumberStyles.Integer, CultureInfo.InvariantCulture, out var chatId)
            ? new NotificationRecipient(null, address, null, chatId)
            : null;
    }

    private async Task<long?> ResolveTelegramChatIdAsync(
        NotificationRecipient recipient,
        int? senderId,
        CancellationToken cancellationToken)
    {
        if (senderId.HasValue && recipient.UserId.HasValue)
        {
            return await _context.NotificationBotSubscriptions
                .AsNoTracking()
                .Where(item =>
                    item.NotificationSenderId == senderId.Value
                    && item.UserId == recipient.UserId.Value
                    && item.IsActive)
                .OrderByDescending(item => item.LastSeenAt)
                .Select(item => (long?)item.ChatId)
                .FirstOrDefaultAsync(cancellationToken);
        }

        return recipient.TelegramChatId;
    }

    private async Task<bool> HasCompletedDeliveryAsync(
        string eventKey,
        string channel,
        NotificationRecipient recipient,
        int? senderId,
        CancellationToken cancellationToken)
    {
        var recipientUserId = recipient.UserId;
        var recipientAddress = ResolveRecipientAddress(recipient, channel);
        return await _context.NotificationDeliveries
            .AsNoTracking()
            .AnyAsync(delivery =>
                delivery.EventKey == eventKey
                && delivery.Channel == channel
                && delivery.NotificationSenderId == senderId
                && delivery.Status != NotificationDeliveryStatus.Failed
                && (recipientUserId.HasValue
                    ? delivery.RecipientUserId == recipientUserId
                    : delivery.RecipientAddress == recipientAddress),
                cancellationToken);
    }

    private async Task CreateUserNotificationAsync(
        int recipientUserId,
        string type,
        string eventKey,
        string title,
        string body,
        string tone,
        TaskJournalItem task,
        object? metadata,
        CancellationToken cancellationToken)
    {
        if (recipientUserId <= 0 || string.IsNullOrWhiteSpace(eventKey))
        {
            return;
        }

        if (!await IsUserNotificationTypeEnabledAsync(recipientUserId, type, cancellationToken))
        {
            return;
        }

        var alreadyExists = await _context.UserNotifications
            .AsNoTracking()
            .AnyAsync(item =>
                item.RecipientUserId == recipientUserId
                && item.EventKey == eventKey,
                cancellationToken);
        if (alreadyExists)
        {
            return;
        }

        var recipientExists = await _context.Users
            .AsNoTracking()
            .AnyAsync(user => user.Id == recipientUserId, cancellationToken);
        if (!recipientExists)
        {
            return;
        }

        _context.UserNotifications.Add(new UserNotification
        {
            RecipientUserId = recipientUserId,
            Type = type,
            EventKey = eventKey,
            Title = Truncate(title.Trim(), 300),
            BodyPreview = Truncate(body.Trim(), 1000),
            Tone = string.IsNullOrWhiteSpace(tone) ? "blue" : tone.Trim(),
            ObjectType = "task",
            ObjectId = task.Id,
            TaskJournalItemId = task.Id,
            MetadataJson = metadata is null ? "{}" : JsonSerializer.Serialize(metadata, JsonOptions),
            CreatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> IsUserNotificationTypeEnabledAsync(
        int recipientUserId,
        string type,
        CancellationToken cancellationToken)
    {
        var setting = await _context.UserSettings
            .AsNoTracking()
            .Where(item =>
                item.UserId == recipientUserId
                && item.Key == UserNotificationPreferenceSettings.SettingKey)
            .Select(item => item.Value)
            .FirstOrDefaultAsync(cancellationToken);

        return UserNotificationPreferenceSettings.IsTypeEnabled(setting, type);
    }

    private async Task<IReadOnlyList<NotificationSenderTarget>> ResolveSenderTargetsAsync(
        string channel,
        string purpose,
        int? senderId,
        CancellationToken cancellationToken)
    {
        var normalizedPurpose = NormalizePurpose(purpose) ?? NotificationPurposes.ManualTest;
        if (senderId.HasValue)
        {
            var sender = await _context.NotificationSenders
                .AsNoTracking()
                .FirstOrDefaultAsync(item => item.Id == senderId.Value, cancellationToken);
            return sender is null || sender.Channel != channel
                ? []
                : [BuildSenderTarget(sender)];
        }

        var channelSenders = await _context.NotificationSenders
            .AsNoTracking()
            .Where(item => item.Channel == channel)
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);

        var matchedSenders = channelSenders
            .Where(item => item.IsEnabled && DeserializeList(item.PurposesJson).Contains(normalizedPurpose, StringComparer.OrdinalIgnoreCase))
            .Select(BuildSenderTarget)
            .ToList();

        if (matchedSenders.Count > 0 || channelSenders.Count > 0)
        {
            return matchedSenders;
        }

        var legacyTarget = await BuildLegacySenderTargetAsync(channel, cancellationToken);
        return legacyTarget is null ? [] : [legacyTarget];
    }

    private async Task<NotificationSenderTarget?> BuildLegacySenderTargetAsync(
        string channel,
        CancellationToken cancellationToken)
    {
        if (channel == NotificationChannels.Email)
        {
            var settings = await LoadSettingsAsync(cancellationToken);
            var email = BuildEmailSettings(settings);
            return new NotificationSenderTarget(
                null,
                channel,
                "Email по умолчанию",
                email.Ready,
                new EmailSendSettings(
                    email.Enabled,
                    email.SmtpHost,
                    email.SmtpPort,
                    email.SmtpUsername,
                    ResolveValue(settings, EmailSmtpPasswordKey, "Email:SmtpPassword") ?? string.Empty,
                    email.FromEmail,
                    email.FromName,
                    email.EnableSsl),
                null,
                null,
                email.Enabled ? "SMTP для email-уведомлений настроен не полностью." : "Email-уведомления отключены.");
        }

        if (channel == NotificationChannels.Telegram)
        {
            var telegram = await _telegramService.GetSettingsAsync(cancellationToken);
            return new NotificationSenderTarget(
                null,
                channel,
                "Telegram по умолчанию",
                telegram.Ready,
                null,
                null,
                null,
                telegram.Enabled ? "Telegram bot token не настроен." : "Telegram-уведомления отключены.");
        }

        return null;
    }

    private static NotificationSenderTarget BuildSenderTarget(NotificationSender sender)
    {
        if (sender.Channel == NotificationChannels.Email)
        {
            var config = DeserializeConfig<EmailSenderConfig>(sender.ConfigJson);
            var ready = sender.IsEnabled
                        && !string.IsNullOrWhiteSpace(config.SmtpHost)
                        && !string.IsNullOrWhiteSpace(config.SmtpUsername)
                        && !string.IsNullOrWhiteSpace(config.SmtpPassword)
                        && !string.IsNullOrWhiteSpace(config.FromEmail);
            return new NotificationSenderTarget(
                sender.Id,
                sender.Channel,
                sender.Name,
                ready,
                new EmailSendSettings(
                    sender.IsEnabled,
                    config.SmtpHost,
                    config.SmtpPort <= 0 ? 587 : config.SmtpPort,
                    config.SmtpUsername,
                    config.SmtpPassword,
                    config.FromEmail,
                    string.IsNullOrWhiteSpace(config.FromName) ? "PayPlanner" : config.FromName,
                    config.EnableSsl),
                null,
                null,
                sender.IsEnabled ? "SMTP отправителя настроен не полностью." : "Отправитель отключён.");
        }

        if (sender.Channel == NotificationChannels.Telegram)
        {
            var config = DeserializeConfig<TelegramSenderConfig>(sender.ConfigJson);
            var ready = sender.IsEnabled && !string.IsNullOrWhiteSpace(config.BotToken);
            return new NotificationSenderTarget(
                sender.Id,
                sender.Channel,
                sender.Name,
                ready,
                null,
                config.BotToken,
                config.ToConnectionSettings(),
                sender.IsEnabled ? "Telegram bot token не настроен." : "Отправитель отключён.");
        }

        return new NotificationSenderTarget(
            sender.Id,
            sender.Channel,
            sender.Name,
            false,
            null,
            null,
            null,
            "Канал отправителя не поддерживается.");
    }

    private async Task<NotificationRecipient> ResolveRecipientAsync(int userId, User fallback, CancellationToken cancellationToken)
    {
        var user = await _context.Users
            .AsNoTracking()
            .Include(item => item.ExternalIdentities)
            .FirstOrDefaultAsync(item => item.Id == userId, cancellationToken);
        return user is null ? BuildRecipient(fallback) : BuildRecipient(user);
    }

    private static NotificationRecipient BuildRecipient(User user)
    {
        var telegramChatId = user.ExternalIdentities
            .Where(identity => identity.Provider == "telegram" && identity.ChatId.HasValue)
            .OrderByDescending(identity => identity.LastUsedAt ?? identity.UpdatedAt)
            .Select(identity => identity.ChatId)
            .FirstOrDefault();

        return new NotificationRecipient(
            user.Id,
            DisplayName(user),
            TechnicalEmailHelper.IsValidRealEmail(user.Email) ? user.Email : null,
            telegramChatId);
    }

    private static string DisplayName(User user)
        => string.IsNullOrWhiteSpace(user.FullName) ? user.Email : user.FullName.Trim();

    private static IReadOnlyList<string> NormalizeChannels(IEnumerable<string>? channels)
    {
        return (channels ?? [NotificationChannels.Email])
            .Select(NormalizeChannel)
            .Where(channel => channel is NotificationChannels.Email or NotificationChannels.Telegram)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToList();
    }

    private static string NormalizeChannel(string? channel)
        => (channel ?? string.Empty).Trim().ToLowerInvariant();

    private static IReadOnlyList<string> NormalizePurposes(IEnumerable<string>? purposes)
    {
        var normalized = (purposes ?? [])
            .Select(NormalizePurpose)
            .Where(purpose => purpose is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return normalized.Count == 0 ? [NotificationPurposes.ManualTest] : normalized;
    }

    private static string? NormalizePurpose(string? purpose)
    {
        var normalized = (purpose ?? string.Empty).Trim();
        return NotificationPurposes.All.Contains(normalized, StringComparer.OrdinalIgnoreCase)
            ? NotificationPurposes.All.First(item => string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase))
            : null;
    }

    private static string NormalizeTelegramUpdateMode(string? mode)
    {
        var normalized = (mode ?? string.Empty).Trim();
        return TelegramBotUpdateModes.All.Contains(normalized, StringComparer.OrdinalIgnoreCase)
            ? TelegramBotUpdateModes.All.First(item => string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase))
            : TelegramBotUpdateModes.Disabled;
    }

    private async Task<int> ResolveNextSenderOrderAsync(string channel, CancellationToken cancellationToken)
    {
        var lastOrder = await _context.NotificationSenders
            .AsNoTracking()
            .Where(item => item.Channel == channel)
            .Select(item => (int?)item.SortOrder)
            .MaxAsync(cancellationToken);
        return (lastOrder ?? 0) + 10;
    }

    private static string DefaultSenderName(string channel)
        => channel == NotificationChannels.Telegram ? "Telegram bot" : "Email отправитель";

    private static string ResolveRecipientAddress(NotificationRecipient recipient, string channel)
        => channel == NotificationChannels.Telegram
            ? recipient.TelegramChatId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty
            : recipient.Email ?? string.Empty;

    private static NotificationSenderDto MapSender(NotificationSender sender, int subscriptionsCount = 0)
    {
        var purposes = DeserializeList(sender.PurposesJson)
            .Select(NormalizePurpose)
            .Where(item => item is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var target = BuildSenderTarget(sender);

        return new NotificationSenderDto
        {
            Id = sender.Id,
            Channel = sender.Channel,
            Name = sender.Name,
            IsEnabled = sender.IsEnabled,
            Ready = target.Ready,
            Purposes = purposes,
            SortOrder = sender.SortOrder,
            Email = sender.Channel == NotificationChannels.Email
                ? MapEmailSenderConfig(DeserializeConfig<EmailSenderConfig>(sender.ConfigJson))
                : null,
            Telegram = sender.Channel == NotificationChannels.Telegram
                ? MapTelegramSenderConfig(DeserializeConfig<TelegramSenderConfig>(sender.ConfigJson), subscriptionsCount)
                : null,
            CreatedAt = sender.CreatedAt,
            UpdatedAt = sender.UpdatedAt
        };
    }

    private static EmailNotificationSenderDto MapEmailSenderConfig(EmailSenderConfig config)
        => new()
        {
            SmtpHost = config.SmtpHost,
            SmtpPort = config.SmtpPort <= 0 ? 587 : config.SmtpPort,
            SmtpUsername = config.SmtpUsername,
            SmtpPasswordSet = !string.IsNullOrWhiteSpace(config.SmtpPassword),
            FromEmail = config.FromEmail,
            FromName = string.IsNullOrWhiteSpace(config.FromName) ? "PayPlanner" : config.FromName,
            EnableSsl = config.EnableSsl
        };

    private static TelegramNotificationSenderDto MapTelegramSenderConfig(TelegramSenderConfig config, int subscriptionsCount)
        => new()
        {
            BotTokenSet = !string.IsNullOrWhiteSpace(config.BotToken),
            BotUsername = config.BotUsername,
            UpdateMode = NormalizeTelegramUpdateMode(config.UpdateMode),
            WebhookSecretSet = !string.IsNullOrWhiteSpace(config.WebhookSecret),
            WebhookUrl = config.WebhookUrl,
            WebhookSyncedAt = config.WebhookSyncedAt,
            LastPollingAt = config.LastPollingAt,
            SubscriptionsCount = subscriptionsCount,
            ProxyUrl = config.ProxyUrl,
            ProxyUsername = config.ProxyUsername,
            ProxyPasswordSet = !string.IsNullOrWhiteSpace(config.ProxyPassword),
            BotId = config.BotId,
            BotFirstName = config.BotFirstName,
            ProfileName = config.ProfileName,
            ProfileDescription = config.ProfileDescription,
            ProfileShortDescription = config.ProfileShortDescription,
            ProfileCommands = config.ProfileCommands
                .Where(item => !string.IsNullOrWhiteSpace(item.Command))
                .Select(item => new TelegramBotCommandDto
                {
                    Command = item.Command,
                    Description = item.Description
                })
                .ToList(),
            HasProfilePhoto = !string.IsNullOrWhiteSpace(config.ProfilePhotoFileId),
            ProfileSyncedAt = config.ProfileSyncedAt,
            CanJoinGroups = config.CanJoinGroups,
            CanReadAllGroupMessages = config.CanReadAllGroupMessages,
            SupportsInlineQueries = config.SupportsInlineQueries
        };

    private static EmailSenderConfig BuildEmailSenderConfig(EmailNotificationSenderRequest? request, string? existingJson)
    {
        var existing = DeserializeConfig<EmailSenderConfig>(existingJson);
        return new EmailSenderConfig
        {
            SmtpHost = NormalizeOptional(request?.SmtpHost) ?? string.Empty,
            SmtpPort = Math.Clamp(request?.SmtpPort ?? existing.SmtpPort, 1, 65535),
            SmtpUsername = NormalizeOptional(request?.SmtpUsername) ?? string.Empty,
            SmtpPassword = request?.KeepExistingPassword == true && string.IsNullOrWhiteSpace(request.SmtpPassword)
                ? existing.SmtpPassword
                : NormalizeOptional(request?.SmtpPassword) ?? string.Empty,
            FromEmail = NormalizeOptional(request?.FromEmail) ?? string.Empty,
            FromName = NormalizeOptional(request?.FromName) ?? "PayPlanner",
            EnableSsl = request?.EnableSsl ?? true
        };
    }

    private static TelegramSenderConfig BuildTelegramSenderConfig(TelegramNotificationSenderRequest? request, string? existingJson)
    {
        var existing = DeserializeConfig<TelegramSenderConfig>(existingJson);
        var updateMode = NormalizeTelegramUpdateMode(request?.UpdateMode ?? existing.UpdateMode);
        var webhookSecret = request?.KeepExistingWebhookSecret == true && string.IsNullOrWhiteSpace(request.WebhookSecret)
            ? existing.WebhookSecret
            : NormalizeOptional(request?.WebhookSecret) ?? existing.WebhookSecret;
        if (updateMode == TelegramBotUpdateModes.Webhook && string.IsNullOrWhiteSpace(webhookSecret))
        {
            webhookSecret = Guid.NewGuid().ToString("N");
        }

        return new TelegramSenderConfig
        {
            BotToken = request?.KeepExistingToken == true && string.IsNullOrWhiteSpace(request.BotToken)
                ? existing.BotToken
                : NormalizeOptional(request?.BotToken) ?? string.Empty,
            BotUsername = NormalizeOptional(request?.BotUsername) ?? existing.BotUsername,
            UpdateMode = updateMode,
            WebhookSecret = webhookSecret,
            WebhookUrl = existing.WebhookUrl,
            WebhookSyncedAt = existing.WebhookSyncedAt,
            LastUpdateId = existing.LastUpdateId,
            LastPollingAt = existing.LastPollingAt,
            ProxyUrl = request?.ProxyUrl is null ? existing.ProxyUrl : NormalizeOptional(request.ProxyUrl) ?? string.Empty,
            ProxyUsername = request?.ProxyUsername is null ? existing.ProxyUsername : NormalizeOptional(request.ProxyUsername) ?? string.Empty,
            ProxyPassword = (request is null || request.KeepExistingProxyPassword) && string.IsNullOrWhiteSpace(request?.ProxyPassword)
                ? existing.ProxyPassword
                : NormalizeOptional(request?.ProxyPassword) ?? string.Empty,
            BotId = existing.BotId,
            BotFirstName = existing.BotFirstName,
            ProfileName = request?.ProfileName ?? existing.ProfileName,
            ProfileDescription = request?.ProfileDescription ?? existing.ProfileDescription,
            ProfileShortDescription = request?.ProfileShortDescription ?? existing.ProfileShortDescription,
            ProfileCommands = request?.ProfileCommands is null
                ? existing.ProfileCommands
                : request.ProfileCommands
                    .Select(item => new TelegramBotCommandConfig
                    {
                        Command = NormalizeCommand(item.Command),
                        Description = item.Description.Trim()
                    })
                    .Where(item => !string.IsNullOrWhiteSpace(item.Command))
                    .ToList(),
            ProfilePhotoFileId = existing.ProfilePhotoFileId,
            ProfileSyncedAt = existing.ProfileSyncedAt,
            CanJoinGroups = existing.CanJoinGroups,
            CanReadAllGroupMessages = existing.CanReadAllGroupMessages,
            SupportsInlineQueries = existing.SupportsInlineQueries
        };
    }

    private static TConfig DeserializeConfig<TConfig>(string? raw)
        where TConfig : new()
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new TConfig();
        }

        try
        {
            return JsonSerializer.Deserialize<TConfig>(raw, JsonOptions) ?? new TConfig();
        }
        catch (JsonException)
        {
            return new TConfig();
        }
    }

    private EmailNotificationSettingsDto BuildEmailSettings(IReadOnlyDictionary<string, string> settings)
    {
        var enabled = ResolveBool(settings, EmailEnabledKey, "Email:Enabled", false);
        var contractTerminationClientEmailsEnabled = ResolveBool(settings, EmailContractTerminationClientEnabledKey, "Email:ContractTerminationClientEmailsEnabled", false);
        var host = ResolveValue(settings, EmailSmtpHostKey, "Email:SmtpHost") ?? string.Empty;
        var port = ResolveInt(settings, EmailSmtpPortKey, "Email:SmtpPort", 587);
        var username = ResolveValue(settings, EmailSmtpUsernameKey, "Email:SmtpUsername") ?? string.Empty;
        var password = ResolveValue(settings, EmailSmtpPasswordKey, "Email:SmtpPassword") ?? string.Empty;
        var fromEmail = ResolveValue(settings, EmailFromEmailKey, "Email:FromEmail") ?? string.Empty;
        var fromName = ResolveValue(settings, EmailFromNameKey, "Email:FromName") ?? "PayPlanner";
        var enableSsl = ResolveBool(settings, EmailEnableSslKey, "Email:EnableSsl", true);

        return new EmailNotificationSettingsDto
        {
            Enabled = enabled,
            SmtpHost = host,
            SmtpPort = port,
            SmtpUsername = username,
            SmtpPasswordSet = !string.IsNullOrWhiteSpace(password),
            FromEmail = fromEmail,
            FromName = fromName,
            EnableSsl = enableSsl,
            ContractTerminationClientEmailsEnabled = contractTerminationClientEmailsEnabled,
            Ready = enabled
                    && !string.IsNullOrWhiteSpace(host)
                    && !string.IsNullOrWhiteSpace(username)
                    && !string.IsNullOrWhiteSpace(password)
                    && !string.IsNullOrWhiteSpace(fromEmail),
        };
    }

    private async Task<Dictionary<string, string>> LoadSettingsAsync(CancellationToken cancellationToken)
    {
        var keys = new[]
        {
            EmailEnabledKey,
            EmailContractTerminationClientEnabledKey,
            EmailSmtpHostKey,
            EmailSmtpPortKey,
            EmailSmtpUsernameKey,
            EmailSmtpPasswordKey,
            EmailFromEmailKey,
            EmailFromNameKey,
            EmailEnableSslKey,
            TelegramEnabledKey,
            TelegramBotTokenKey,
            RegistrationNotifyAdminsKey,
        };

        return await _context.SystemSettings
            .AsNoTracking()
            .Where(setting => keys.Contains(setting.Key))
            .ToDictionaryAsync(setting => setting.Key, setting => setting.Value, cancellationToken);
    }

    private async Task UpsertSettingAsync(string key, string value, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var setting = await _context.SystemSettings.FirstOrDefaultAsync(item => item.Key == key, cancellationToken);
        if (setting is null)
        {
            _context.SystemSettings.Add(new SystemSetting
            {
                Key = key,
                Value = value,
                CreatedAt = nowUtc,
                UpdatedAt = nowUtc,
            });
            return;
        }

        setting.Value = value;
        setting.UpdatedAt = nowUtc;
    }

    private async Task<bool> ShouldNotifyAdminsOnRegistrationAsync(CancellationToken cancellationToken)
    {
        var settings = await LoadSettingsAsync(cancellationToken);
        return ResolveBool(
            settings,
            RegistrationNotifyAdminsKey,
            "Features:NotifyAdminsOnRegistration",
            false);
    }

    private string? ResolveValue(IReadOnlyDictionary<string, string> settings, string settingKey, string configPath)
    {
        if (settings.TryGetValue(settingKey, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            return value.Trim();
        }

        var configured = _configuration[configPath];
        return string.IsNullOrWhiteSpace(configured) ? null : configured.Trim();
    }

    private bool ResolveBool(IReadOnlyDictionary<string, string> settings, string settingKey, string configPath, bool fallback)
    {
        var raw = ResolveValue(settings, settingKey, configPath);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => fallback
        };
    }

    private int ResolveInt(IReadOnlyDictionary<string, string> settings, string settingKey, string configPath, int fallback)
    {
        var raw = ResolveValue(settings, settingKey, configPath);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : fallback;
    }

    private static string BuildTaskReminderSubject(TaskJournalItem task, string reminderKey, DateTime nowUtc)
        => reminderKey switch
        {
            "dailyOverdue" => $"Просрочена задача: {task.Title}",
            "atDue" => $"Наступил срок задачи: {task.Title}",
            _ => $"Напоминание о задаче: {task.Title}"
        };

    private static string BuildTaskReminderBody(TaskJournalItem task, string reminderKey, DateTime nowUtc)
    {
        var dueText = DateTime.SpecifyKind(task.DueAt, DateTimeKind.Utc).ToString("dd.MM.yyyy HH:mm", RuCulture);
        var prefix = reminderKey switch
        {
            "dailyOverdue" => "Задача просрочена.",
            "atDue" => "Наступил срок выполнения задачи.",
            "before15m" => "До срока задачи осталось около 15 минут.",
            "before1h" => "До срока задачи остался примерно 1 час.",
            "before1d" => "До срока задачи остался примерно 1 день.",
            _ => "Напоминание о задаче."
        };

        var lines = new List<string>
        {
            prefix,
            string.Empty,
            $"Объект: {task.ObjectName}",
            $"Задача: {task.Title}",
            $"Срок: {dueText}",
            $"Приоритет: {task.Priority}",
        };

        if (!string.IsNullOrWhiteSpace(task.Description))
        {
            lines.Add(string.Empty);
            lines.Add(task.Description.Trim());
        }

        return string.Join('\n', lines);
    }

    private static string BuildTaskAssignedBody(TaskJournalItem task, string actorName)
    {
        var dueText = DateTime.SpecifyKind(task.DueAt, DateTimeKind.Utc).ToString("dd.MM.yyyy HH:mm", RuCulture);
        var lines = new List<string>
        {
            $"{actorName} назначил(а) задачу.",
            string.Empty,
            $"Клиент/дело: {task.ObjectName}",
            $"Задача: {task.Title}",
            $"Срок: {dueText}",
            $"Приоритет: {task.Priority}",
        };

        if (!string.IsNullOrWhiteSpace(task.AssigneeName))
        {
            lines.Add($"Исполнитель: {task.AssigneeName.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(task.Description))
        {
            lines.Add(string.Empty);
            lines.Add(task.Description.Trim());
        }

        return string.Join('\n', lines);
    }

    private static string BuildTaskCompletedBody(TaskJournalItem task)
    {
        var dueText = DateTime.SpecifyKind(task.DueAt, DateTimeKind.Utc).ToString("dd.MM.yyyy HH:mm", RuCulture);
        var lines = new List<string>
        {
            "Исполнитель отметил задачу выполненной.",
            string.Empty,
            $"Клиент/дело: {task.ObjectName}",
            $"Задача: {task.Title}",
            $"Срок: {dueText}",
        };

        if (!string.IsNullOrWhiteSpace(task.AssigneeName))
        {
            lines.Add($"Исполнитель: {task.AssigneeName.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(task.Result))
        {
            lines.Add(string.Empty);
            lines.Add($"Результат: {task.Result.Trim()}");
        }

        return string.Join('\n', lines);
    }

    private static string BuildCaseInactivityControlBody(TaskJournalItem task, int daysWithoutActiveTask)
    {
        var dueText = DateTime.SpecifyKind(task.DueAt, DateTimeKind.Utc).ToString("dd.MM.yyyy HH:mm", RuCulture);
        var lines = new List<string>
        {
            "По делу давно нет активной задачи, поэтому автоконтроль создал контрольную задачу.",
            string.Empty,
            $"Клиент/дело: {task.ObjectName}",
            $"Задача: {task.Title}",
            $"Нет активных задач: {Math.Max(0, daysWithoutActiveTask)} дн.",
            $"Срок: {dueText}",
            "Проверьте состояние дела и зафиксируйте следующий шаг."
        };

        if (!string.IsNullOrWhiteSpace(task.AssigneeName))
        {
            lines.Add($"Исполнитель: {task.AssigneeName.Trim()}");
        }

        return string.Join('\n', lines);
    }

    private static string BuildTaskScheduleConflictBody(TaskJournalItem task, IReadOnlyList<TaskJournalItem> conflicts, string actorName)
    {
        var dueText = DateTime.SpecifyKind(task.DueAt, DateTimeKind.Utc).ToString("dd.MM.yyyy HH:mm", RuCulture);
        var lines = new List<string>
        {
            $"{actorName} назначил(а) или изменил(а) задачу на время, которое пересекается с другими активными задачами.",
            string.Empty,
            $"Клиент/дело: {task.ObjectName}",
            $"Задача: {task.Title}",
            $"Срок: {dueText}",
        };

        if (!string.IsNullOrWhiteSpace(task.AssigneeName))
        {
            lines.Add($"Исполнитель: {task.AssigneeName.Trim()}");
        }

        lines.Add(string.Empty);
        lines.Add("Пересечения:");
        foreach (var conflict in conflicts)
        {
            var conflictDue = DateTime.SpecifyKind(conflict.DueAt, DateTimeKind.Utc).ToString("dd.MM.yyyy HH:mm", RuCulture);
            lines.Add($"- {conflictDue}: {conflict.Title}");
        }

        return string.Join('\n', lines);
    }

    private static string BuildTaskApprovalRequestedBody(TaskJournalItem task, string actorName, TaskJournalParticipant? approver)
    {
        var dueText = DateTime.SpecifyKind(task.DueAt, DateTimeKind.Utc).ToString("dd.MM.yyyy HH:mm", RuCulture);
        var lines = new List<string>
        {
            $"{actorName} отправил(а) задачу на согласование.",
            string.Empty,
            $"Клиент/дело: {task.ObjectName}",
            $"Задача: {task.Title}",
            $"Срок: {dueText}",
        };

        var approverName = ResolveParticipantName(approver);
        if (!string.IsNullOrWhiteSpace(approverName))
        {
            lines.Add($"Текущий согласующий: {approverName}");
        }

        if (!string.IsNullOrWhiteSpace(task.Result))
        {
            lines.Add(string.Empty);
            lines.Add($"Результат для проверки: {task.Result.Trim()}");
        }

        return string.Join('\n', lines);
    }

    private static string BuildTaskApprovalDecisionBody(
        TaskJournalItem task,
        TaskJournalParticipant approver,
        string decision,
        string actorName,
        string? resolution)
    {
        var dueText = DateTime.SpecifyKind(task.DueAt, DateTimeKind.Utc).ToString("dd.MM.yyyy HH:mm", RuCulture);
        var isRevision = string.Equals(decision, "revisionRequested", StringComparison.OrdinalIgnoreCase);
        var actionText = isRevision ? "вернул(а) задачу на доработку" : "согласовал(а) задачу";
        var lines = new List<string>
        {
            $"{actorName} {actionText}.",
            string.Empty,
            $"Клиент/дело: {task.ObjectName}",
            $"Задача: {task.Title}",
            $"Срок: {dueText}",
        };

        var approverName = ResolveParticipantName(approver);
        if (!string.IsNullOrWhiteSpace(approverName))
        {
            lines.Add($"Согласующий: {approverName}");
        }

        if (!string.IsNullOrWhiteSpace(resolution))
        {
            lines.Add(string.Empty);
            lines.Add($"Резолюция: {resolution.Trim()}");
        }

        return string.Join('\n', lines);
    }

    private static string BuildTaskChangedBody(
        TaskJournalItem task,
        IReadOnlyList<string> changeLabels,
        string actorName,
        string? comment)
    {
        var dueText = DateTime.SpecifyKind(task.DueAt, DateTimeKind.Utc).ToString("dd.MM.yyyy HH:mm", RuCulture);
        var lines = new List<string>
        {
            $"{actorName} изменил(а) задачу.",
            string.Empty,
            $"Клиент/дело: {task.ObjectName}",
            $"Задача: {task.Title}",
            $"Срок: {dueText}",
        };

        if (changeLabels.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Изменения:");
            foreach (var changeLabel in changeLabels)
            {
                lines.Add($"- {changeLabel}");
            }
        }

        if (!string.IsNullOrWhiteSpace(comment))
        {
            lines.Add(string.Empty);
            lines.Add($"Комментарий: {comment.Trim()}");
        }

        return string.Join('\n', lines);
    }

    private static string BuildTaskCommentBody(TaskJournalItem task, TaskJournalComment comment)
    {
        var dueText = DateTime.SpecifyKind(task.DueAt, DateTimeKind.Utc).ToString("dd.MM.yyyy HH:mm", RuCulture);
        return string.Join('\n', new[]
        {
            $"{comment.AuthorName} добавил(а) комментарий по задаче.",
            string.Empty,
            $"Клиент/дело: {task.ObjectName}",
            $"Задача: {task.Title}",
            $"Срок: {dueText}",
            string.Empty,
            comment.Text.Trim()
        });
    }

    private static bool HasTaskChannel(TaskJournalItem task, string channel)
        => DeserializeList(task.ChannelsJson).Contains(channel, StringComparer.OrdinalIgnoreCase);

    private static bool IsReminderOverdue(TaskJournalItem task, string reminderKey, DateTime nowUtc)
    {
        var dueAt = DateTime.SpecifyKind(task.DueAt, DateTimeKind.Utc);
        return string.Equals(reminderKey, "dailyOverdue", StringComparison.OrdinalIgnoreCase)
            || string.Equals(reminderKey, "atDue", StringComparison.OrdinalIgnoreCase)
            || nowUtc >= dueAt;
    }

    private static IReadOnlyList<string> ResolveTaskMessageChannels(TaskJournalItem task)
        => DeserializeList(task.ChannelsJson)
            .Where(channel => channel is NotificationChannels.Email or NotificationChannels.Telegram)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static IReadOnlyList<int> ResolveTaskNotificationUserIds(TaskJournalItem task, int actorUserId)
    {
        var userIds = new List<int>();
        if (task.AuthorUserId.HasValue)
        {
            userIds.Add(task.AuthorUserId.Value);
        }

        if (task.AssigneeUserId.HasValue)
        {
            userIds.Add(task.AssigneeUserId.Value);
        }

        userIds.AddRange(task.Participants
            .Where(participant => participant.UserId.HasValue)
            .Select(participant => participant.UserId!.Value));

        return userIds
            .Where(userId => userId > 0 && userId != actorUserId)
            .Distinct()
            .ToList();
    }

    private static IReadOnlyList<int> ResolveCurrentApproverUserIds(TaskJournalItem task, int actorUserId)
    {
        var currentApprover = GetCurrentApprover(task);
        if (currentApprover?.UserId is not { } userId || userId <= 0 || userId == actorUserId)
        {
            return Array.Empty<int>();
        }

        return [userId];
    }

    private static IReadOnlyList<int> ResolveApprovalRequestRecipientUserIds(TaskJournalItem task, int actorUserId)
    {
        var userIds = new List<int>();
        if (task.AuthorUserId.HasValue)
        {
            userIds.Add(task.AuthorUserId.Value);
        }

        if (task.AssigneeUserId.HasValue)
        {
            userIds.Add(task.AssigneeUserId.Value);
        }

        userIds.AddRange(task.Participants
            .Where(participant => !string.Equals(participant.Role, "approver", StringComparison.OrdinalIgnoreCase))
            .Where(participant => participant.UserId.HasValue)
            .Select(participant => participant.UserId!.Value));

        if (GetCurrentApprover(task)?.UserId is { } approverUserId)
        {
            userIds.Add(approverUserId);
        }

        return userIds
            .Where(userId => userId > 0 && userId != actorUserId)
            .Distinct()
            .ToList();
    }

    private static IReadOnlyList<int> ResolveApprovalDecisionRecipientUserIds(TaskJournalItem task, int actorUserId)
    {
        var userIds = new List<int>();
        if (task.AuthorUserId.HasValue)
        {
            userIds.Add(task.AuthorUserId.Value);
        }

        if (task.AssigneeUserId.HasValue)
        {
            userIds.Add(task.AssigneeUserId.Value);
        }

        userIds.AddRange(task.Participants
            .Where(participant => !string.Equals(participant.Role, "approver", StringComparison.OrdinalIgnoreCase))
            .Where(participant => participant.UserId.HasValue)
            .Select(participant => participant.UserId!.Value));

        return userIds
            .Where(userId => userId > 0 && userId != actorUserId)
            .Distinct()
            .ToList();
    }

    private static bool IsTaskWaitingForNextApprover(TaskJournalItem task)
        => task.Participants.Any(participant =>
            string.Equals(participant.Role, "approver", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(participant.ApprovalStatus, "approved", StringComparison.OrdinalIgnoreCase));

    private static TaskJournalParticipant? GetCurrentApprover(TaskJournalItem task)
        => task.Participants
            .Where(participant => string.Equals(participant.Role, "approver", StringComparison.OrdinalIgnoreCase))
            .OrderBy(participant => participant.SortOrder)
            .ThenBy(participant => participant.Id)
            .FirstOrDefault(participant => !string.Equals(participant.ApprovalStatus, "approved", StringComparison.OrdinalIgnoreCase));

    private static string ResolveParticipantName(TaskJournalParticipant? participant)
        => participant is null
            ? string.Empty
            : !string.IsNullOrWhiteSpace(participant.DisplayName)
                ? participant.DisplayName.Trim()
                : participant.User?.FullName?.Trim() ?? participant.User?.Email?.Trim() ?? string.Empty;

    private static IReadOnlyList<string> DeserializeList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<string>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(raw, JsonOptions) ?? [];
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string NormalizeCommand(string? value)
        => (value ?? string.Empty).Trim().TrimStart('/').ToLowerInvariant();
}
