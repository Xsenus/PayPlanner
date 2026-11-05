using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.LegalIntegrations;
using PayPlanner.Api.Services.Tasks;

namespace PayPlanner.Api.Services.Notifications;

public sealed partial class NotificationService : INotificationService, ICaseDocumentNotificationLiveDispatcher, IDurableTaskStatusNotificationService, IDurablePaymentOverdueNotificationService
{
    public const string CourtEventLeaderUserIdSettingKey = "notifications.courtEventLeaderUserId";
    private static readonly SemaphoreSlim SmsRateLimitGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly CultureInfo RuCulture = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly IReadOnlyList<NotificationPurposeDefinition> PurposeDefinitions =
    [
        new(NotificationPurposes.TelegramAuth, "Вход через Telegram", "Telegram-бот для авторизации, привязки профиля и Login Widget.", [NotificationChannels.Telegram]),
        new(NotificationPurposes.RegistrationUser, "Регистрация пользователя", "Сообщения пользователю о заявке, одобрении или отклонении регистрации.", [NotificationChannels.Email, NotificationChannels.Telegram]),
        new(NotificationPurposes.RegistrationAdmin, "Новая регистрация для админов", "Уведомления администраторам о новой заявке на регистрацию.", [NotificationChannels.Email, NotificationChannels.Telegram]),
        new(NotificationPurposes.PasswordIssue, "Выдача пароля", "Служебные сообщения с новым временным паролем сотрудника.", [NotificationChannels.Email, NotificationChannels.Telegram]),
        new(NotificationPurposes.TaskReminder, "Напоминания по задачам", "Плановые уведомления из журнала задач.", [NotificationChannels.Email, NotificationChannels.Telegram]),
        new(NotificationPurposes.ContractClient, "Письма клиентам по договорам", "Автоматические сообщения клиентам по событиям договора.", [NotificationChannels.Email]),
        new(NotificationPurposes.ManualTest, "Ручные и тестовые сообщения", "Проверочные отправки из настроек уведомлений.", [NotificationChannels.Email, NotificationChannels.Telegram])
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
    private const string NotificationTemplatesKey = "notifications.templates.v1";

    private readonly PaymentContext _context;
    private readonly IConfiguration _configuration;
    private readonly IEmailService _emailService;
    private readonly ITelegramNotificationService _telegramService;
    private readonly ISmsNotificationService? _smsService;
    private readonly IMaxNotificationService? _maxService;
    private readonly IMaxOutboxService? _maxOutboxService;
    private Task<MaxRuntimeSettings>? _maxRuntimeSettingsTask;
    private readonly IRealtimeNotificationPublisher _realtimePublisher;
    private readonly IWebPushNotificationService? _webPushNotificationService;
    private readonly ILogger<NotificationService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private SmsDeliveryPolicy? _smsDeliveryPolicyCache;

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

    private sealed class NotificationTemplateStorage
    {
        public List<NotificationTemplateStorageItem> Templates { get; set; } = [];
    }

    private sealed class NotificationTemplateStorageItem
    {
        public string Key { get; set; } = string.Empty;
        public bool IsEnabled { get; set; } = true;
        public string Subject { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
    }

    public NotificationService(
        PaymentContext context,
        IConfiguration configuration,
        IEmailService emailService,
        ITelegramNotificationService telegramService,
        ILogger<NotificationService> logger,
        IRealtimeNotificationPublisher? realtimePublisher = null,
        IWebPushNotificationService? webPushNotificationService = null,
        ISmsNotificationService? smsService = null,
        TimeProvider? timeProvider = null,
        IHttpContextAccessor? httpContextAccessor = null,
        IMaxNotificationService? maxService = null,
        IMaxOutboxService? maxOutboxService = null)
    {
        _context = context;
        _configuration = configuration;
        _emailService = emailService;
        _telegramService = telegramService;
        _smsService = smsService;
        _maxService = maxService;
        _maxOutboxService = maxOutboxService;
        _realtimePublisher = realtimePublisher ?? NoOpRealtimeNotificationPublisher.Instance;
        _webPushNotificationService = webPushNotificationService;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _httpContextAccessor = httpContextAccessor;
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
            Sms = _smsService is null
                ? new SmsNotificationSettingsDto
                {
                    Enabled = false,
                    Ready = false,
                    ProviderConfigured = false,
                    AdapterConfigured = false,
                    ApiTokenSet = false,
                    StatusText = "SMS-уведомления отключены: SMS-сервис не подключен."
                }
                : await _smsService.GetSettingsAsync(cancellationToken),
            Max = _maxService is null
                ? await BuildMaxDiagnosticsAsync(cancellationToken)
                : await _maxService.GetSettingsAsync(cancellationToken),
            Senders = senders.Select(sender => MapSender(sender, subscriptionCounts.GetValueOrDefault(sender.Id))).ToList(),
            Purposes = PurposeDefinitions.Select(item => new NotificationPurposeDto
            {
                Key = item.Key,
                Label = item.Label,
                Description = item.Description,
                Channels = item.Channels
            }).ToList(),
            Templates = await GetTemplatesAsync(cancellationToken)
        };
    }

    private async Task<MaxNotificationDiagnosticsDto> BuildMaxDiagnosticsAsync(CancellationToken cancellationToken)
    {
        var lastSuccessfulDeliveryAt = await _context.NotificationDeliveries
            .AsNoTracking()
            .Where(item => item.Channel == NotificationChannels.Max && item.Status == NotificationDeliveryStatus.Sent)
            .OrderByDescending(item => item.SentAt ?? item.CreatedAt)
            .Select(item => item.SentAt ?? item.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        var lastFailedDelivery = await _context.NotificationDeliveries
            .AsNoTracking()
            .Where(item => item.Channel == NotificationChannels.Max && item.Status == NotificationDeliveryStatus.Failed)
            .OrderByDescending(item => item.CreatedAt)
            .Select(item => new { item.CreatedAt, item.Error })
            .FirstOrDefaultAsync(cancellationToken);

        return new MaxNotificationDiagnosticsDto
        {
            Enabled = false,
            Ready = false,
            ApiConfirmed = false,
            BotConfigured = false,
            ScenariosConfirmed = false,
            AllowedEventTypes = Array.Empty<string>(),
            LastSuccessfulDeliveryAt = lastSuccessfulDeliveryAt == default ? null : lastSuccessfulDeliveryAt,
            LastFailedDeliveryAt = lastFailedDelivery?.CreatedAt,
            LastDeliveryError = NotificationDeliveryPresentation.ResolveError(
                NotificationChannels.Max,
                lastFailedDelivery?.Error),
            StatusText = "MAX-уведомления не включены: API, бот и сценарии использования еще не подтверждены заказчиком."
        };
    }

    public Task<IReadOnlyList<NotificationChannelDto>> GetChannelsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<NotificationChannelDto> channels = NotificationChannels.Definitions
            .Select(item => new NotificationChannelDto
            {
                Key = item.Key,
                Label = item.Label,
                Description = item.Description,
                Status = item.Status,
                IsEnabled = item.IsEnabled,
                IsSendable = item.IsSendable,
                SupportsUserPreferences = item.SupportsUserPreferences,
                SupportsDeliveryLog = item.SupportsDeliveryLog,
                RequiresProvider = item.RequiresProvider,
                IsFuture = item.IsFuture
            })
            .ToList();

        return Task.FromResult(channels);
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

    public async Task<NotificationSettingsDto> UpdateSmsSettingsAsync(
        UpdateSmsNotificationSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_smsService is null)
        {
            throw new InvalidOperationException("SmsNotificationServiceMissing");
        }

        await _smsService.UpdateSettingsAsync(request, cancellationToken);
        _smsDeliveryPolicyCache = null;
        return await GetSettingsAsync(cancellationToken);
    }

    public async Task<NotificationSettingsDto> UpdateMaxSettingsAsync(
        UpdateMaxNotificationSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_maxService is null)
        {
            throw new InvalidOperationException("MaxNotificationServiceMissing");
        }

        var previous = await _maxService.GetSettingsAsync(cancellationToken);
        var next = await _maxService.UpdateSettingsAsync(request, cancellationToken);
        var auditEvent = NotificationSettingsAudit.CreateMaxSettingsUpdatedEvent(
            _httpContextAccessor?.HttpContext?.User,
            previous,
            next,
            !request.KeepExistingBotToken || !string.IsNullOrWhiteSpace(request.BotToken),
            !request.KeepExistingWebhookSecret || !string.IsNullOrWhiteSpace(request.WebhookSecret));
        if (auditEvent is not null)
        {
            _context.ObjectAuditEvents.Add(auditEvent);
            await _context.SaveChangesAsync(cancellationToken);
        }
        return await GetSettingsAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<NotificationTemplateDto>> GetTemplatesAsync(CancellationToken cancellationToken = default)
    {
        var storage = await LoadTemplateStorageAsync(cancellationToken);
        return NotificationTemplateCatalog.All.Select(definition => MapTemplate(definition, storage)).ToList();
    }

    public async Task<NotificationTemplateDto> UpdateTemplateAsync(
        string key,
        UpdateNotificationTemplateRequest request,
        CancellationToken cancellationToken = default)
    {
        var definition = NotificationTemplateCatalog.Find(key)
            ?? throw new InvalidOperationException("NotificationTemplateNotFound");
        var storage = await LoadTemplateStorageAsync(cancellationToken);
        var item = storage.Templates.FirstOrDefault(template => string.Equals(template.Key, definition.Key, StringComparison.OrdinalIgnoreCase));
        var previous = MapTemplate(definition, storage);
        if (item is null)
        {
            item = new NotificationTemplateStorageItem { Key = definition.Key };
            storage.Templates.Add(item);
        }

        item.IsEnabled = request.IsEnabled;
        item.Subject = NormalizeTemplateText(request.Subject, definition.DefaultSubject);
        item.Body = NormalizeTemplateText(request.Body, definition.DefaultBody);

        var next = MapTemplate(definition, storage);
        var auditEvent = NotificationSettingsAudit.CreateSmsTemplateUpdatedEvent(
            _httpContextAccessor?.HttpContext?.User,
            previous,
            next,
            reset: false);
        if (auditEvent is not null)
        {
            _context.ObjectAuditEvents.Add(auditEvent);
        }

        await SaveTemplateStorageAsync(storage, cancellationToken);
        return next;
    }

    public async Task<NotificationTemplateDto> ResetTemplateAsync(string key, CancellationToken cancellationToken = default)
    {
        var definition = NotificationTemplateCatalog.Find(key)
            ?? throw new InvalidOperationException("NotificationTemplateNotFound");
        var storage = await LoadTemplateStorageAsync(cancellationToken);
        var previous = MapTemplate(definition, storage);
        storage.Templates.RemoveAll(template => string.Equals(template.Key, definition.Key, StringComparison.OrdinalIgnoreCase));
        var next = MapTemplate(definition, storage);
        var auditEvent = NotificationSettingsAudit.CreateSmsTemplateUpdatedEvent(
            _httpContextAccessor?.HttpContext?.User,
            previous,
            next,
            reset: true);
        if (auditEvent is not null)
        {
            _context.ObjectAuditEvents.Add(auditEvent);
        }

        await SaveTemplateStorageAsync(storage, cancellationToken);
        return next;
    }

    public async Task<NotificationMessage> RenderTemplateAsync(
        string key,
        string fallbackSubject,
        string fallbackBody,
        IReadOnlyDictionary<string, string?> values,
        bool isSensitive = false,
        string purpose = NotificationPurposes.ManualTest,
        CancellationToken cancellationToken = default)
    {
        var definition = NotificationTemplateCatalog.Find(key);
        if (definition is null)
        {
            return new NotificationMessage(fallbackSubject, fallbackBody, IsSensitive: isSensitive, Purpose: purpose);
        }

        var storage = await LoadTemplateStorageAsync(cancellationToken);
        return RenderTemplate(
            definition,
            storage,
            fallbackSubject,
            fallbackBody,
            values,
            isSensitive,
            purpose);
    }

    private static NotificationMessage RenderTemplate(
        NotificationTemplateDefinition? definition,
        NotificationTemplateStorage storage,
        string fallbackSubject,
        string fallbackBody,
        IReadOnlyDictionary<string, string?> values,
        bool isSensitive,
        string purpose)
    {
        if (definition is null)
        {
            return new NotificationMessage(fallbackSubject, fallbackBody, IsSensitive: isSensitive, Purpose: purpose);
        }

        var item = storage.Templates.FirstOrDefault(template => string.Equals(template.Key, definition.Key, StringComparison.OrdinalIgnoreCase));
        if (item is { IsEnabled: false })
        {
            return new NotificationMessage(fallbackSubject, fallbackBody, IsSensitive: isSensitive, Purpose: purpose);
        }

        var subjectTemplate = string.IsNullOrWhiteSpace(item?.Subject) ? definition.DefaultSubject : item.Subject;
        var bodyTemplate = string.IsNullOrWhiteSpace(item?.Body) ? definition.DefaultBody : item.Body;
        var subject = NotificationTemplateCatalog.Render(subjectTemplate, values);
        var body = NotificationTemplateCatalog.CompactBlankLines(NotificationTemplateCatalog.Render(bodyTemplate, values));

        return new NotificationMessage(
            string.IsNullOrWhiteSpace(subject) ? fallbackSubject : subject,
            string.IsNullOrWhiteSpace(body) ? fallbackBody : body,
            IsSensitive: isSensitive,
            Purpose: purpose);
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
        sender.PurposesJson = JsonSerializer.Serialize(NormalizePurposes(channel, request.Purposes), JsonOptions);
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
        var userValues = BuildUserTemplateValues(user);
        var pendingTemplateDefinition = NotificationTemplateCatalog.Find(NotificationTemplateKeys.RegistrationPending);
        var adminTemplateDefinition = NotificationTemplateCatalog.Find(NotificationTemplateKeys.RegistrationAdminNewUser);
        var templateStorage = pendingTemplateDefinition is null && adminTemplateDefinition is null
            ? new NotificationTemplateStorage()
            : await LoadTemplateStorageAsync(cancellationToken);
        var pendingMessage = RenderTemplate(
            pendingTemplateDefinition,
            templateStorage,
            "Регистрация ожидает подтверждения",
            $"Здравствуйте, {DisplayName(user)}!\n\nВаша заявка на регистрацию получена и ожидает проверки администратором.",
            userValues,
            isSensitive: false,
            purpose: NotificationPurposes.RegistrationUser);
        deliveries.AddRange(await SendAsync(
            recipient,
            pendingMessage,
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
            var adminMessage = RenderTemplate(
                adminTemplateDefinition,
                templateStorage,
                "Новая регистрация в CRM",
                $"В системе зарегистрировался новый пользователь.\n\nИмя: {DisplayName(user)}\nEmail: {user.Email}",
                userValues,
                isSensitive: false,
                purpose: NotificationPurposes.RegistrationAdmin);

            foreach (var admin in admins)
            {
                deliveries.AddRange(await SendAsync(
                    BuildRecipient(admin),
                    adminMessage,
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
        var message = await RenderTemplateAsync(
            NotificationTemplateKeys.RegistrationApproved,
            "Регистрация одобрена",
            $"Здравствуйте, {DisplayName(user)}!\n\nДоступ к PayPlanner одобрен. Теперь можно войти в систему.",
            BuildUserTemplateValues(user),
            purpose: NotificationPurposes.RegistrationUser,
            cancellationToken: cancellationToken);
        return await SendAsync(
            recipient,
            message,
            [NotificationChannels.Email, NotificationChannels.Telegram],
            $"registration:{user.Id}:approved:{DateTime.UtcNow:yyyyMMddHHmmss}",
            cancellationToken);
    }

    public async Task<IReadOnlyList<NotificationDelivery>> NotifyRegistrationRejectedAsync(User user, string? reason, CancellationToken cancellationToken = default)
    {
        var reasonText = string.IsNullOrWhiteSpace(reason) ? "Причина не указана." : reason.Trim();
        var recipient = await ResolveRecipientAsync(user.Id, user, cancellationToken);
        var message = await RenderTemplateAsync(
            NotificationTemplateKeys.RegistrationRejected,
            "Регистрация отклонена",
            $"Здравствуйте, {DisplayName(user)}!\n\nЗаявка на регистрацию отклонена.\nПричина: {reasonText}",
            NotificationTemplateCatalog.MergeValues(
                BuildUserTemplateValues(user),
                new Dictionary<string, string?> { ["registration.reason"] = reasonText }),
            purpose: NotificationPurposes.RegistrationUser,
            cancellationToken: cancellationToken);
        return await SendAsync(
            recipient,
            message,
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
        var message = await RenderTemplateAsync(
            NotificationTemplateKeys.PasswordIssued,
            "Выдан новый пароль",
            $"Здравствуйте, {DisplayName(user)}!\n\nДля вашей учетной записи выдан новый пароль:\n{password}\n\nПосле входа рекомендуем сменить его в профиле.",
            NotificationTemplateCatalog.MergeValues(
                BuildUserTemplateValues(user),
                new Dictionary<string, string?> { ["password"] = password }),
            isSensitive: true,
            purpose: NotificationPurposes.PasswordIssue,
            cancellationToken: cancellationToken);
        return await SendAsync(
            recipient,
            message,
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
        var recipientUserIds = ResolveTaskAssigneeUserIds(task);
        if (recipientUserIds.Count == 0)
        {
            return Array.Empty<NotificationDelivery>();
        }

        var inAppEnabled = HasTaskChannel(task, NotificationChannels.InApp);
        var channels = ResolveTaskMessageChannels(task);
        var dueAt = DateTime.SpecifyKind(task.DueAt, DateTimeKind.Utc);
        var dueAtKey = dueAt.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var recipientTimeZones = await CalendarTimeZoneHelper.ResolveUserTimeZonesAsync(
            _context,
            recipientUserIds,
            cancellationToken);
        var recipientNotificationChannels = await ResolveUserNotificationChannelsAsync(
            recipientUserIds,
            UserNotificationTypes.TaskReminder,
            channels.Concat([
                NotificationChannels.InApp,
                NotificationChannels.Toast,
                NotificationChannels.WebPush,
            ]),
            cancellationToken, task: task);
        var externalRecipientIds = recipientUserIds
            .Where(userId => channels.Any(recipientNotificationChannels[userId].Contains))
            .ToList();
        var externalRecipients = await ResolveRecipientsAsync(externalRecipientIds, cancellationToken);
        var templateDefinition = NotificationTemplateCatalog.Find(NotificationTemplateKeys.TaskReminder);
        var templateStorage = templateDefinition is null
            ? new NotificationTemplateStorage()
            : await LoadTemplateStorageAsync(cancellationToken);
        var deliveries = new List<NotificationDelivery>();

        foreach (var recipientUserId in recipientUserIds)
        {
            var recipientTimeZone = recipientTimeZones[recipientUserId];
            var eventKey = reminderKey == "dailyOverdue"
                ? $"task:{task.Id}:reminder:{reminderKey}:{CalendarTimeZoneHelper.ToUserLocal(nowUtc, recipientTimeZone):yyyyMMdd}"
                : $"task:{task.Id}:reminder:{reminderKey}:{dueAtKey}";
            var reminderSubject = BuildTaskReminderSubject(task, reminderKey, nowUtc);
            var reminderBody = BuildTaskReminderBody(task, reminderKey, nowUtc, recipientTimeZone);
            var message = RenderTemplate(
                templateDefinition,
                templateStorage,
                reminderSubject,
                reminderBody,
                NotificationTemplateCatalog.MergeValues(
                    BuildTaskTemplateValues(task, recipientTimeZone),
                    new Dictionary<string, string?>
                    {
                        ["reminder.subject"] = reminderSubject,
                        ["reminder.body"] = reminderBody,
                    }),
                isSensitive: false,
                purpose: NotificationPurposes.TaskReminder);

            if (inAppEnabled)
            {
                await CreateUserNotificationAsync(
                    recipientUserId,
                    UserNotificationTypes.TaskReminder,
                    eventKey,
                    message.Subject,
                    message.Body,
                    IsReminderOverdue(task, reminderKey, nowUtc) ? "rose" : "amber",
                    task,
                    new { reminderKey, dueAt = task.DueAt },
                    cancellationToken,
                    recipientNotificationChannels[recipientUserId]);
            }

            var enabledChannels = channels
                .Where(recipientNotificationChannels[recipientUserId].Contains)
                .ToList();
            if (enabledChannels.Count > 0 && externalRecipients.TryGetValue(recipientUserId, out var recipient))
            {
                deliveries.AddRange(await SendAsync(recipient, message, enabledChannels, eventKey, cancellationToken));
            }
        }

        return deliveries;
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

        var eventKey = $"task:{task.Id}:assigned";
        var tone = ResolveTaskAssignedTone(task);
        var isUrgent = string.Equals(tone, "rose", StringComparison.OrdinalIgnoreCase);
        var recipientTimeZones = await CalendarTimeZoneHelper.ResolveUserTimeZonesAsync(
            _context,
            recipients,
            cancellationToken);
        var recipientNotificationChannels = await ResolveUserNotificationChannelsAsync(
            recipients,
            UserNotificationTypes.TaskAssigned,
            channels.Concat([
                NotificationChannels.InApp,
                NotificationChannels.Toast,
                NotificationChannels.WebPush,
            ]),
            cancellationToken, task: task);
        var externalDeliveryRecipientIds = recipients
            .Where(userId => channels.Any(recipientNotificationChannels[userId].Contains))
            .ToList();
        var externalDeliveryRecipients = await ResolveRecipientsAsync(
            externalDeliveryRecipientIds,
            cancellationToken);
        var templateDefinition = NotificationTemplateCatalog.Find(NotificationTemplateKeys.TaskAssigned);
        var templateStorage = templateDefinition is null
            ? new NotificationTemplateStorage()
            : await LoadTemplateStorageAsync(cancellationToken);
        var deliveries = new List<NotificationDelivery>();
        foreach (var userId in recipients)
        {
            var recipientTimeZone = recipientTimeZones[userId];
            const string taskAssignedSubject = "Новая задача";
            var taskAssignedBody = BuildTaskAssignedBody(task, actorName, recipientTimeZone);
            var message = RenderTemplate(
                templateDefinition,
                templateStorage,
                taskAssignedSubject,
                taskAssignedBody,
                BuildTaskAssignmentTemplateValues(task, recipientTimeZone, actorName, taskAssignedSubject),
                isSensitive: false,
                purpose: NotificationPurposes.TaskReminder);

            if (HasTaskChannel(task, NotificationChannels.InApp))
            {
                await CreateUserNotificationAsync(
                    userId,
                    UserNotificationTypes.TaskAssigned,
                    eventKey,
                    message.Subject,
                    message.Body,
                    tone,
                    task,
                    new { actorUserId, actorName, task.AssigneeUserId, task.AssigneeName, isUrgent },
                    cancellationToken,
                    recipientNotificationChannels[userId]);
            }

            var enabledChannels = channels
                .Where(recipientNotificationChannels[userId].Contains)
                .ToList();
            if (enabledChannels.Count > 0)
            {
                if (externalDeliveryRecipients.TryGetValue(userId, out var recipient))
                {
                    deliveries.AddRange(await SendAsync(recipient, message, enabledChannels, eventKey, cancellationToken));
                }
            }
        }

        return deliveries;
    }

    public async Task<IReadOnlyList<NotificationDelivery>> NotifyTaskReassignedAsync(
        TaskJournalItem task,
        int? previousAssigneeUserId,
        string? previousAssigneeName,
        IReadOnlyList<string> changeLabels,
        string actorName,
        int actorUserId,
        string? comment = null,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<int>? additionalRecipientUserIds = null)
    {
        var channels = ResolveTaskMessageChannels(task);
        var currentAssigneeUserId = task.AssigneeUserId;
        var allRecipients = ResolveTaskNotificationUserIds(task, actorUserId)
            .Concat(additionalRecipientUserIds ?? Array.Empty<int>())
            .Where(userId => userId > 0 && userId != actorUserId)
            .Distinct()
            .ToList();
        var timeZoneRecipientIds = allRecipients.ToHashSet();
        if (currentAssigneeUserId.HasValue && currentAssigneeUserId.Value != actorUserId)
        {
            timeZoneRecipientIds.Add(currentAssigneeUserId.Value);
        }

        if (previousAssigneeUserId.HasValue
            && previousAssigneeUserId.Value != actorUserId
            && previousAssigneeUserId.Value != currentAssigneeUserId)
        {
            timeZoneRecipientIds.Add(previousAssigneeUserId.Value);
        }

        var recipientTimeZones = await CalendarTimeZoneHelper.ResolveUserTimeZonesAsync(
            _context,
            timeZoneRecipientIds,
            cancellationToken);
        var remainingRecipients = allRecipients
            .Where(userId => userId != currentAssigneeUserId && userId != previousAssigneeUserId)
            .ToList();
        var currentAssigneeRecipients = currentAssigneeUserId.HasValue
                                        && currentAssigneeUserId.Value != actorUserId
            ? new[] { currentAssigneeUserId.Value }
            : Array.Empty<int>();
        var changedRecipients = remainingRecipients.ToHashSet();
        if (previousAssigneeUserId.HasValue
            && previousAssigneeUserId.Value != actorUserId
            && previousAssigneeUserId.Value != currentAssigneeUserId)
        {
            changedRecipients.Add(previousAssigneeUserId.Value);
        }

        var currentAssigneeNotificationChannels = await ResolveUserNotificationChannelsAsync(
            currentAssigneeRecipients,
            UserNotificationTypes.TaskAssigned,
            channels.Concat([
                NotificationChannels.InApp,
                NotificationChannels.Toast,
                NotificationChannels.WebPush,
            ]),
            cancellationToken, task: task);
        var changedRecipientNotificationChannels = await ResolveUserNotificationChannelsAsync(
            changedRecipients,
            UserNotificationTypes.TaskChanged,
            channels.Concat([
                NotificationChannels.InApp,
                NotificationChannels.Toast,
                NotificationChannels.WebPush,
            ]),
            cancellationToken, task: task);
        var deliveryRecipientIds = currentAssigneeRecipients
            .Where(userId => channels.Any(currentAssigneeNotificationChannels[userId].Contains))
            .Concat(changedRecipients.Where(userId => channels.Any(changedRecipientNotificationChannels[userId].Contains)))
            .Distinct()
            .ToList();
        var deliveryRecipients = await ResolveRecipientsAsync(deliveryRecipientIds, cancellationToken);
        var assignedTemplateDefinition = NotificationTemplateCatalog.Find(NotificationTemplateKeys.TaskAssigned);
        var changedTemplateDefinition = NotificationTemplateCatalog.Find(NotificationTemplateKeys.TaskChanged);
        var templateStorage = assignedTemplateDefinition is null && changedTemplateDefinition is null
            ? new NotificationTemplateStorage()
            : await LoadTemplateStorageAsync(cancellationToken);
        var eventStamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        var deliveries = new List<NotificationDelivery>();

        if (currentAssigneeUserId.HasValue && currentAssigneeUserId.Value != actorUserId)
        {
            var userId = currentAssigneeUserId.Value;
            var recipientTimeZone = recipientTimeZones[userId];
            var subject = $"Задача назначена вам: {task.Title}";
            var body = BuildTaskReassignedToNewAssigneeBody(task, changeLabels, actorName, previousAssigneeName, comment, recipientTimeZone);
            var message = RenderTemplate(
                assignedTemplateDefinition,
                templateStorage,
                subject,
                body,
                BuildTaskAssignmentTemplateValues(
                    task,
                    recipientTimeZone,
                    actorName,
                    subject,
                    $"Предыдущий исполнитель: {FormatNotificationName(previousAssigneeName)}\nНовый исполнитель: {FormatNotificationName(task.AssigneeName)}"),
                isSensitive: false,
                purpose: NotificationPurposes.TaskReminder);
            var eventKey = $"task:{task.Id}:reassigned-to:{userId}:{eventStamp}";

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
                    new { actorUserId, actorName, previousAssigneeUserId, previousAssigneeName, task.AssigneeUserId, task.AssigneeName, isReassignment = true },
                    cancellationToken,
                    currentAssigneeNotificationChannels[userId]);
            }

            var enabledChannels = channels
                .Where(currentAssigneeNotificationChannels[userId].Contains)
                .ToList();
            if (enabledChannels.Count > 0)
            {
                if (deliveryRecipients.TryGetValue(userId, out var recipient))
                {
                    deliveries.AddRange(await SendAsync(recipient, message, enabledChannels, eventKey, cancellationToken));
                }
            }
        }

        if (previousAssigneeUserId.HasValue
            && previousAssigneeUserId.Value != actorUserId
            && previousAssigneeUserId.Value != currentAssigneeUserId)
        {
            var userId = previousAssigneeUserId.Value;
            var recipientTimeZone = recipientTimeZones[userId];
            var recipientChangeLabels = FormatTaskChangeLabels(changeLabels, recipientTimeZone);
            var subject = $"Задача снята с вас: {task.Title}";
            var body = BuildTaskReassignedFromPreviousAssigneeBody(task, recipientChangeLabels, actorName, previousAssigneeName, comment, recipientTimeZone);
            var message = RenderTemplate(
                changedTemplateDefinition,
                templateStorage,
                subject,
                body,
                NotificationTemplateCatalog.MergeValues(
                    BuildTaskTemplateValues(task, recipientTimeZone, actorName),
                    new Dictionary<string, string?>
                    {
                        ["task.changes"] = string.Join(Environment.NewLine, OrderTaskChangeLabels(recipientChangeLabels).Select(label => $"- {label}")),
                        ["task.commentBlock"] = string.IsNullOrWhiteSpace(comment) ? string.Empty : $"Комментарий: {comment.Trim()}",
                        ["task.changeSubject"] = subject,
                    }),
                isSensitive: false,
                purpose: NotificationPurposes.TaskReminder);
            var eventKey = $"task:{task.Id}:reassigned-from:{userId}:{eventStamp}";

            if (HasTaskChannel(task, NotificationChannels.InApp))
            {
                await CreateUserNotificationAsync(
                    userId,
                    UserNotificationTypes.TaskChanged,
                    eventKey,
                    message.Subject,
                    message.Body,
                    "amber",
                    task,
                    new { actorUserId, actorName, previousAssigneeUserId, previousAssigneeName, task.AssigneeUserId, task.AssigneeName, hasAssigneeChange = true },
                    cancellationToken,
                    changedRecipientNotificationChannels[userId]);
            }

            var enabledChannels = channels
                .Where(changedRecipientNotificationChannels[userId].Contains)
                .ToList();
            if (enabledChannels.Count > 0)
            {
                if (deliveryRecipients.TryGetValue(userId, out var recipient))
                {
                    deliveries.AddRange(await SendAsync(recipient, message, enabledChannels, eventKey, cancellationToken));
                }
            }
        }

        foreach (var userId in remainingRecipients)
        {
            var recipientTimeZone = recipientTimeZones[userId];
            var recipientChangeLabels = FormatTaskChangeLabels(changeLabels, recipientTimeZone);
            var subject = $"Изменен исполнитель задачи: {task.Title}";
            var body = BuildTaskChangedBody(task, recipientChangeLabels, actorName, comment, recipientTimeZone);
            var message = RenderTemplate(
                changedTemplateDefinition,
                templateStorage,
                subject,
                body,
                NotificationTemplateCatalog.MergeValues(
                    BuildTaskTemplateValues(task, recipientTimeZone, actorName),
                    new Dictionary<string, string?>
                    {
                        ["task.changes"] = string.Join(Environment.NewLine, OrderTaskChangeLabels(recipientChangeLabels).Select(label => $"- {label}")),
                        ["task.commentBlock"] = string.IsNullOrWhiteSpace(comment) ? string.Empty : $"Комментарий: {comment.Trim()}",
                        ["task.changeSubject"] = subject,
                    }),
                isSensitive: false,
                purpose: NotificationPurposes.TaskReminder);
            var eventKey = $"task:{task.Id}:reassigned-observer:{userId}:{eventStamp}";

            if (HasTaskChannel(task, NotificationChannels.InApp))
            {
                await CreateUserNotificationAsync(
                    userId,
                    UserNotificationTypes.TaskChanged,
                    eventKey,
                    message.Subject,
                    message.Body,
                    "amber",
                    task,
                    new { actorUserId, actorName, changeLabels, previousAssigneeUserId, previousAssigneeName, task.AssigneeUserId, task.AssigneeName, hasAssigneeChange = true },
                    cancellationToken,
                    changedRecipientNotificationChannels[userId]);
            }

            var enabledChannels = channels
                .Where(changedRecipientNotificationChannels[userId].Contains)
                .ToList();
            if (enabledChannels.Count > 0)
            {
                if (deliveryRecipients.TryGetValue(userId, out var recipient))
                {
                    deliveries.AddRange(await SendAsync(recipient, message, enabledChannels, eventKey, cancellationToken));
                }
            }
        }

        return deliveries;
    }

    public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskCompletedAsync(
        TaskJournalItem task,
        CancellationToken cancellationToken = default)
        => NotifyCompletionForEventCoreAsync(task, $"task:{task.Id}:completed", false, cancellationToken);

    public Task<IReadOnlyList<NotificationDelivery>> NotifyCompletionForEventAsync(
        TaskJournalItem task, string eventKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventKey);
        return NotifyCompletionForEventCoreAsync(task, eventKey, true, cancellationToken);
    }

    private async Task<IReadOnlyList<NotificationDelivery>> NotifyCompletionForEventCoreAsync(
        TaskJournalItem task, string eventKey, bool retryMaxQueue, CancellationToken cancellationToken)
    {
        if (!task.AuthorUserId.HasValue)
        {
            return Array.Empty<NotificationDelivery>();
        }

        if (retryMaxQueue && !(await ResolveDurableTaskRecipientsAsync(task.Id, [task.AuthorUserId.Value], cancellationToken, authorOnly: true)).Contains(task.AuthorUserId.Value))
        {
            return Array.Empty<NotificationDelivery>();
        }

        var recipientTimeZone = await ResolveTaskRecipientTimeZoneAsync(task.AuthorUserId.Value, cancellationToken);
        var taskCompletedSubject = $"Задача выполнена: {task.Title}";
        var taskCompletedBody = BuildTaskCompletedBody(task, recipientTimeZone);
        var message = await RenderTemplateAsync(
            NotificationTemplateKeys.TaskCompleted,
            taskCompletedSubject,
            taskCompletedBody,
            BuildTaskTemplateValues(task, recipientTimeZone),
            purpose: NotificationPurposes.TaskReminder,
            cancellationToken: cancellationToken);

        if (HasTaskChannel(task, NotificationChannels.InApp))
        {
            await CreateUserNotificationAsync(
                task.AuthorUserId.Value,
                UserNotificationTypes.TaskCompleted,
                eventKey,
                message.Subject,
                message.Body,
                "blue",
                task,
                new { status = task.Status },
                cancellationToken,
                retryMaxQueue: retryMaxQueue,
                replayRealtimeOnRetry: retryMaxQueue);
        }

        if (retryMaxQueue && !await IsDurableTaskRecipientCurrentAsync(task, task.AuthorUserId.Value, true, cancellationToken))
            return Array.Empty<NotificationDelivery>();

        var enabledChannels = await ResolveUserNotificationChannelsAsync(
            task.AuthorUserId.Value,
            UserNotificationTypes.TaskCompleted,
            [NotificationChannels.Email, NotificationChannels.Telegram],
            cancellationToken, task: task);
        return enabledChannels.Count == 0
            ? Array.Empty<NotificationDelivery>()
            : await SendToUserAsync(
                task.AuthorUserId.Value,
                message,
                enabledChannels,
                eventKey,
                cancellationToken);
    }

    public async Task<IReadOnlyList<NotificationDelivery>> NotifyTaskCancelledAsync(
        TaskJournalItem task,
        string actorName,
        int actorUserId,
        string action,
        CancellationToken cancellationToken = default)
    {
        var recipients = ResolveTaskNotificationUserIds(task, actorUserId);
        if (recipients.Count == 0)
        {
            return Array.Empty<NotificationDelivery>();
        }

        var normalizedAction = string.IsNullOrWhiteSpace(action) ? "Отменена" : action.Trim();
        var subject = $"Задача отменена: {task.Title}";
        var eventKey = $"task:{task.Id}:cancelled:{NormalizeEventKeySegment(normalizedAction)}";
        var channels = ResolveTaskMessageChannels(task);
        var recipientTimeZones = await CalendarTimeZoneHelper.ResolveUserTimeZonesAsync(
            _context,
            recipients,
            cancellationToken);
        var recipientNotificationChannels = await ResolveUserNotificationChannelsAsync(
            recipients,
            UserNotificationTypes.TaskCancelled,
            channels.Concat([
                NotificationChannels.InApp,
                NotificationChannels.Toast,
                NotificationChannels.WebPush,
            ]),
            cancellationToken, task: task);
        var deliveryRecipientIds = recipients
            .Where(userId => channels.Any(recipientNotificationChannels[userId].Contains))
            .ToList();
        var deliveryRecipients = await ResolveRecipientsAsync(deliveryRecipientIds, cancellationToken);
        var templateDefinition = NotificationTemplateCatalog.Find(NotificationTemplateKeys.TaskCancelled);
        var templateStorage = templateDefinition is null
            ? new NotificationTemplateStorage()
            : await LoadTemplateStorageAsync(cancellationToken);
        var deliveries = new List<NotificationDelivery>();
        foreach (var userId in recipients)
        {
            var recipientTimeZone = recipientTimeZones[userId];
            var body = BuildTaskCancelledBody(task, actorName, normalizedAction, recipientTimeZone);
            var message = RenderTemplate(
                templateDefinition,
                templateStorage,
                subject,
                body,
                NotificationTemplateCatalog.MergeValues(
                    BuildTaskTemplateValues(task, recipientTimeZone, actorName),
                    new Dictionary<string, string?> { ["task.cancelAction"] = normalizedAction }),
                isSensitive: false,
                purpose: NotificationPurposes.TaskReminder);

            if (HasTaskChannel(task, NotificationChannels.InApp))
            {
                await CreateUserNotificationAsync(
                    userId,
                    UserNotificationTypes.TaskCancelled,
                    eventKey,
                    message.Subject,
                    message.Body,
                    "orange",
                    "task",
                    task.Id,
                    null,
                    task,
                    new { actorUserId, actorName, action = normalizedAction },
                    cancellationToken,
                    resolvedNotificationChannels: recipientNotificationChannels[userId]);
            }

            var enabledChannels = channels
                .Where(recipientNotificationChannels[userId].Contains)
                .ToList();
            if (enabledChannels.Count > 0)
            {
                if (deliveryRecipients.TryGetValue(userId, out var recipient))
                {
                    deliveries.AddRange(await SendAsync(recipient, message, enabledChannels, eventKey, cancellationToken));
                }
            }
        }

        return deliveries;
    }

    public async Task NotifyCalendarAccessChangedAsync(
        int roleId,
        IReadOnlyCollection<int> addedVisibleUserIds,
        IReadOnlyCollection<int> removedVisibleUserIds,
        string actorName,
        int actorUserId,
        CancellationToken cancellationToken = default)
    {
        if (roleId <= 0 || (addedVisibleUserIds.Count == 0 && removedVisibleUserIds.Count == 0))
        {
            return;
        }

        var recipients = await _context.Users
            .AsNoTracking()
            .Where(user =>
                user.RoleId == roleId
                && user.IsActive
                && user.IsApproved
                && user.Id != actorUserId)
            .Select(user => user.Id)
            .ToListAsync(cancellationToken);
        if (recipients.Count == 0)
        {
            return;
        }

        var calendarUserIds = addedVisibleUserIds
            .Concat(removedVisibleUserIds)
            .Where(userId => userId > 0)
            .Distinct()
            .ToArray();
        var namesById = await _context.Users
            .AsNoTracking()
            .Where(user => calendarUserIds.Contains(user.Id))
            .Select(user => new
            {
                user.Id,
                Name = user.FullName == "" ? user.Email : user.FullName
            })
            .ToDictionaryAsync(item => item.Id, item => item.Name, cancellationToken);

        var addedText = FormatCalendarAccessUserList(addedVisibleUserIds, namesById);
        var removedText = FormatCalendarAccessUserList(removedVisibleUserIds, namesById);
        var subject = "Изменена видимость календарей";
        var body = BuildCalendarAccessChangedBody(actorName, addedText, removedText);
        var eventKey = $"calendar-access:{roleId}:{string.Join('-', addedVisibleUserIds.OrderBy(id => id))}:{string.Join('-', removedVisibleUserIds.OrderBy(id => id))}:{DateTime.UtcNow:yyyyMMddHHmmssfff}";
        var message = await RenderTemplateAsync(
            NotificationTemplateKeys.CalendarAccessChanged,
            subject,
            body,
            new Dictionary<string, string?>
            {
                ["actor.name"] = actorName,
                ["calendar.added"] = addedText,
                ["calendar.removed"] = removedText,
            },
            purpose: NotificationPurposes.TaskReminder,
            cancellationToken: cancellationToken);
        var recipientNotificationChannels = await ResolveUserNotificationChannelsAsync(
            recipients,
            UserNotificationTypes.CalendarAccessChanged,
            [
                NotificationChannels.InApp,
                NotificationChannels.Toast,
                NotificationChannels.WebPush,
            ],
            cancellationToken);

        foreach (var userId in recipients)
        {
            await CreateUserNotificationAsync(
                userId,
                UserNotificationTypes.CalendarAccessChanged,
                eventKey,
                message.Subject,
                message.Body,
                "violet",
                "calendar",
                roleId,
                null,
                null,
                new { roleId, actorUserId, actorName, addedVisibleUserIds, removedVisibleUserIds },
                cancellationToken,
                resolvedNotificationChannels: recipientNotificationChannels[userId]);
        }
    }

    public async Task NotifyExternalCalendarSyncWarningAsync(
        string providerName,
        string operation,
        string summary,
        string? details = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedProviderName = NormalizeNotificationText(providerName, "Внешний календарь");
        var normalizedOperation = NormalizeNotificationText(operation, "синхронизация");
        var normalizedSummary = NormalizeNotificationText(summary, "Ошибка синхронизации внешнего календаря");
        var normalizedDetails = string.IsNullOrWhiteSpace(details) ? string.Empty : Truncate(details.Trim(), 700);
        var eventKey = $"external-calendar-sync:{NormalizeEventKeySegment(normalizedProviderName)}:{NormalizeEventKeySegment(normalizedOperation)}:{DateTime.UtcNow:yyyyMMdd}";

        var admins = await _context.Users
            .AsNoTracking()
            .Include(item => item.Role)
            .Where(item =>
                item.IsActive
                && item.IsApproved
                && item.Role != null
                && item.Role.Name == "admin")
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        if (admins.Count == 0)
        {
            return;
        }

        var detailsBlock = string.IsNullOrWhiteSpace(normalizedDetails)
            ? string.Empty
            : $"Детали: {normalizedDetails}\n\n";
        var message = await RenderTemplateAsync(
            NotificationTemplateKeys.ExternalCalendarSyncWarning,
            $"Проблема синхронизации календаря: {normalizedProviderName}",
            BuildExternalCalendarSyncWarningBody(normalizedProviderName, normalizedOperation, normalizedSummary, normalizedDetails),
            new Dictionary<string, string?>
            {
                ["calendar.provider"] = normalizedProviderName,
                ["calendar.operation"] = normalizedOperation,
                ["calendar.summary"] = normalizedSummary,
                ["calendar.details"] = normalizedDetails,
                ["calendar.detailsBlock"] = detailsBlock,
            },
            purpose: NotificationPurposes.ManualTest,
            cancellationToken: cancellationToken);
        var adminNotificationChannels = await ResolveUserNotificationChannelsAsync(
            admins,
            UserNotificationTypes.SystemAdminWarning,
            [
                NotificationChannels.InApp,
                NotificationChannels.Toast,
                NotificationChannels.WebPush,
            ],
            cancellationToken);

        foreach (var adminId in admins)
        {
            await CreateUserNotificationAsync(
                adminId,
                UserNotificationTypes.SystemAdminWarning,
                eventKey,
                message.Subject,
                message.Body,
                "rose",
                "system",
                null,
                null,
                null,
                new
                {
                    providerName = normalizedProviderName,
                    operation = normalizedOperation,
                    summary = normalizedSummary,
                    details = normalizedDetails
                },
                cancellationToken,
                resolvedNotificationChannels: adminNotificationChannels[adminId]);
        }
    }

    public async Task<int> NotifySmsChannelWarningAsync(
        string eventKey,
        string issueCode,
        string title,
        string body,
        CancellationToken cancellationToken = default)
    {
        var normalizedEventKey = $"sms-channel-monitor:{NormalizeEventKeySegment(eventKey)}";
        var normalizedIssueCode = NormalizeEventKeySegment(issueCode);
        var normalizedTitle = NormalizeNotificationText(title, "SMS-канал требует внимания");
        var normalizedBody = NormalizeNotificationText(body, "Проверьте настройки SMS-канала.");
        var admins = await _context.Users
            .AsNoTracking()
            .Where(item =>
                item.IsActive
                && item.IsApproved
                && item.Role != null
                && item.Role.Name == "admin")
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        if (admins.Count == 0)
        {
            return 0;
        }

        var adminNotificationChannels = await ResolveUserNotificationChannelsAsync(
            admins,
            UserNotificationTypes.SystemAdminWarning,
            [
                NotificationChannels.InApp,
                NotificationChannels.Toast,
                NotificationChannels.WebPush,
            ],
            cancellationToken);
        var existingRecipientIds = await _context.UserNotifications
            .AsNoTracking()
            .Where(item =>
                item.EventKey == normalizedEventKey
                && admins.Contains(item.RecipientUserId))
            .Select(item => item.RecipientUserId)
            .ToHashSetAsync(cancellationToken);
        var metadataJson = JsonSerializer.Serialize(
            new
            {
                source = "sms-channel-monitoring",
                issueCode = normalizedIssueCode,
            },
            JsonOptions);
        var createdAt = DateTime.UtcNow;
        var notifications = admins
            .Where(adminId => !existingRecipientIds.Contains(adminId))
            .Where(adminId => adminNotificationChannels[adminId].Contains(NotificationChannels.InApp))
            .Select(adminId => new UserNotification
            {
                RecipientUserId = adminId,
                Type = UserNotificationTypes.SystemAdminWarning,
                EventKey = normalizedEventKey,
                Title = Truncate(normalizedTitle, 300),
                BodyPreview = Truncate(normalizedBody, 1000),
                Tone = "rose",
                ObjectType = "system",
                MetadataJson = metadataJson,
                CreatedAt = createdAt,
            })
            .ToList();
        if (notifications.Count == 0)
        {
            return 0;
        }

        _context.UserNotifications.AddRange(notifications);
        await _context.SaveChangesAsync(cancellationToken);
        foreach (var notification in notifications)
        {
            await QueueMaxNotificationIfAvailableAsync(notification, cancellationToken);
            var channels = adminNotificationChannels[notification.RecipientUserId];
            await _realtimePublisher.PublishUserNotificationAsync(
                notification,
                channels.Contains(NotificationChannels.Toast),
                cancellationToken);
            if (_webPushNotificationService is not null
                && channels.Contains(NotificationChannels.WebPush))
            {
                await _webPushNotificationService.SendUserNotificationAsync(notification, cancellationToken);
            }
        }

        return notifications.Count;
    }

    public async Task NotifyStickyNoteMentionAsync(
        int noteId,
        int boardId,
        string boardTitle,
        string noteText,
        string actorName,
        int actorUserId,
        IReadOnlyCollection<int> recipientUserIds,
        CancellationToken cancellationToken = default)
    {
        if (noteId <= 0 || boardId <= 0 || recipientUserIds.Count == 0)
        {
            return;
        }

        var recipients = recipientUserIds
            .Where(userId => userId > 0 && !UserNotificationAntiSpamPolicy.ShouldSuppressSelfNotification(userId, actorUserId))
            .Distinct()
            .ToList();
        if (recipients.Count == 0)
        {
            return;
        }

        var normalizedBoardTitle = NormalizeNotificationText(boardTitle, "Заметки");
        var normalizedActorName = NormalizeNotificationText(actorName, "Сотрудник");
        var preview = BuildStickyNotePreview(noteText);
        var textHash = BuildStableTextHash(noteText);
        var message = await RenderTemplateAsync(
            NotificationTemplateKeys.NoteMention,
            "Вас упомянули в заметке",
            BuildStickyNoteMentionBody(normalizedActorName, normalizedBoardTitle, preview),
            new Dictionary<string, string?>
            {
                ["actor.name"] = normalizedActorName,
                ["note.boardTitle"] = normalizedBoardTitle,
                ["note.preview"] = preview,
            },
            purpose: NotificationPurposes.ManualTest,
            cancellationToken: cancellationToken);
        var recipientNotificationChannels = await ResolveUserNotificationChannelsAsync(
            recipients,
            UserNotificationTypes.NoteMention,
            [
                NotificationChannels.InApp,
                NotificationChannels.Toast,
                NotificationChannels.WebPush,
            ],
            cancellationToken);

        foreach (var recipientUserId in recipients)
        {
            await CreateUserNotificationAsync(
                recipientUserId,
                UserNotificationTypes.NoteMention,
                $"note:{noteId}:mention:{recipientUserId}:{textHash}",
                message.Subject,
                message.Body,
                "cyan",
                "note",
                noteId,
                null,
                null,
                new
                {
                    noteId,
                    boardId,
                    boardTitle = normalizedBoardTitle,
                    actorUserId,
                    actorName = normalizedActorName
                },
                cancellationToken,
                resolvedNotificationChannels: recipientNotificationChannels[recipientUserId]);
        }
    }

    public async Task NotifyStickyNoteAssignedAsync(
        int noteId,
        int boardId,
        string boardTitle,
        string noteText,
        int assignedUserId,
        string actorName,
        int actorUserId,
        DateTime? reminderAt,
        CancellationToken cancellationToken = default)
    {
        if (noteId <= 0
            || boardId <= 0
            || assignedUserId <= 0
            || UserNotificationAntiSpamPolicy.ShouldSuppressSelfNotification(assignedUserId, actorUserId))
        {
            return;
        }

        var normalizedBoardTitle = NormalizeNotificationText(boardTitle, "Заметки");
        var normalizedActorName = NormalizeNotificationText(actorName, "Сотрудник");
        var preview = BuildStickyNotePreview(noteText);
        var timeZone = await ResolveTaskRecipientTimeZoneAsync(assignedUserId, cancellationToken);
        var reminderText = FormatStickyNoteReminderAt(reminderAt, timeZone);
        var eventKey = $"note:{noteId}:assigned:{assignedUserId}:{FormatStickyNoteEventDate(reminderAt)}";
        var message = await RenderTemplateAsync(
            NotificationTemplateKeys.NoteAssigned,
            "Вам назначена заметка",
            BuildStickyNoteAssignedBody(normalizedActorName, normalizedBoardTitle, preview, reminderText),
            new Dictionary<string, string?>
            {
                ["actor.name"] = normalizedActorName,
                ["note.boardTitle"] = normalizedBoardTitle,
                ["note.reminderAt"] = reminderText,
                ["note.preview"] = preview,
            },
            purpose: NotificationPurposes.ManualTest,
            cancellationToken: cancellationToken);

        await CreateUserNotificationAsync(
            assignedUserId,
            UserNotificationTypes.NoteAssigned,
            eventKey,
            message.Subject,
            message.Body,
            "violet",
            "note",
            noteId,
            null,
            null,
            new
            {
                noteId,
                boardId,
                boardTitle = normalizedBoardTitle,
                actorUserId,
                actorName = normalizedActorName,
                reminderAt
            },
            cancellationToken);
    }

    public async Task NotifyStickyNoteReminderAsync(
        StickyNote note,
        CancellationToken cancellationToken = default)
    {
        if (note.Id <= 0 || !note.AssignedUserId.HasValue || !note.ReminderAt.HasValue)
        {
            return;
        }

        var recipientUserId = note.AssignedUserId.Value;
        var boardTitle = NormalizeNotificationText(note.Board?.Title, "Заметки");
        var preview = BuildStickyNotePreview(note.Text);
        var timeZone = await ResolveTaskRecipientTimeZoneAsync(recipientUserId, cancellationToken);
        var reminderText = FormatStickyNoteReminderAt(note.ReminderAt, timeZone);
        var eventKey = $"note:{note.Id}:reminder:{recipientUserId}:{FormatStickyNoteEventDate(note.ReminderAt)}";
        var message = await RenderTemplateAsync(
            NotificationTemplateKeys.NoteReminder,
            "Напоминание по заметке",
            BuildStickyNoteReminderBody(boardTitle, preview, reminderText),
            new Dictionary<string, string?>
            {
                ["note.boardTitle"] = boardTitle,
                ["note.reminderAt"] = reminderText,
                ["note.preview"] = preview,
            },
            purpose: NotificationPurposes.TaskReminder,
            cancellationToken: cancellationToken);

        await CreateUserNotificationAsync(
            recipientUserId,
            UserNotificationTypes.NoteReminder,
            eventKey,
            message.Subject,
            message.Body,
            "amber",
            "note",
            note.Id,
            null,
            null,
            new
            {
                noteId = note.Id,
                boardId = note.BoardId,
                boardTitle,
                reminderAt = note.ReminderAt
            },
            cancellationToken);
    }

    public async Task NotifyStickyNoteImportantChangeAsync(
        int noteId,
        int boardId,
        string boardTitle,
        string noteText,
        string action,
        string actorName,
        int actorUserId,
        IReadOnlyCollection<int> recipientUserIds,
        int noteVersion,
        CancellationToken cancellationToken = default)
    {
        if (noteId <= 0 || boardId <= 0 || recipientUserIds.Count == 0)
        {
            return;
        }

        var recipients = recipientUserIds
            .Where(userId => userId > 0 && !UserNotificationAntiSpamPolicy.ShouldSuppressSelfNotification(userId, actorUserId))
            .Distinct()
            .ToList();
        if (recipients.Count == 0)
        {
            return;
        }

        var normalizedBoardTitle = NormalizeNotificationText(boardTitle, "Общее");
        var normalizedActorName = NormalizeNotificationText(actorName, "Сотрудник");
        var normalizedAction = NormalizeStickyNoteImportantAction(action);
        var preview = BuildStickyNotePreview(noteText);
        var eventAction = NormalizeStickyNoteImportantActionKey(action);
        var eventVersion = Math.Max(1, noteVersion);
        var message = await RenderTemplateAsync(
            NotificationTemplateKeys.NoteImportantChanged,
            "Важная заметка на общей доске",
            BuildStickyNoteImportantChangeBody(normalizedActorName, normalizedAction, normalizedBoardTitle, preview),
            new Dictionary<string, string?>
            {
                ["actor.name"] = normalizedActorName,
                ["note.action"] = normalizedAction,
                ["note.boardTitle"] = normalizedBoardTitle,
                ["note.preview"] = preview,
            },
            purpose: NotificationPurposes.ManualTest,
            cancellationToken: cancellationToken);
        var recipientNotificationChannels = await ResolveUserNotificationChannelsAsync(
            recipients,
            UserNotificationTypes.NoteImportantChanged,
            [
                NotificationChannels.InApp,
                NotificationChannels.Toast,
                NotificationChannels.WebPush,
            ],
            cancellationToken);

        foreach (var recipientUserId in recipients)
        {
            await CreateUserNotificationAsync(
                recipientUserId,
                UserNotificationTypes.NoteImportantChanged,
                $"note:{noteId}:important:{eventAction}:{eventVersion}:{recipientUserId}",
                message.Subject,
                message.Body,
                "orange",
                "note",
                noteId,
                null,
                null,
                new
                {
                    noteId,
                    boardId,
                    boardTitle = normalizedBoardTitle,
                    action = eventAction,
                    actorUserId,
                    actorName = normalizedActorName
                },
                cancellationToken,
                resolvedNotificationChannels: recipientNotificationChannels[recipientUserId]);
        }
    }

    public async Task NotifyPaymentAssignedAsync(
        Payment payment,
        string actorName,
        int actorUserId,
        IReadOnlyCollection<int> recipientUserIds,
        CancellationToken cancellationToken = default)
    {
        if (payment.Id <= 0 || recipientUserIds.Count == 0)
        {
            return;
        }

        var visibleRecipientUserIds = await ResolveVisiblePaymentNotificationRecipientIdsAsync(
            payment,
            RolePermissionCatalog.Payments,
            recipientUserIds,
            actorUserId,
            cancellationToken);
        if (visibleRecipientUserIds.Count == 0)
        {
            return;
        }

        var normalizedActorName = NormalizeNotificationText(actorName, "Сотрудник");
        var message = await RenderTemplateAsync(
            NotificationTemplateKeys.PaymentAssigned,
            "Вам назначен платеж",
            BuildPaymentAssignedBody(payment, normalizedActorName),
            BuildPaymentTemplateValues(payment, normalizedActorName, []),
            purpose: NotificationPurposes.ManualTest,
            cancellationToken: cancellationToken);
        var recipientNotificationChannels = await ResolveUserNotificationChannelsAsync(
            visibleRecipientUserIds,
            UserNotificationTypes.PaymentAssigned,
            [
                NotificationChannels.InApp,
                NotificationChannels.Toast,
                NotificationChannels.WebPush,
            ],
            cancellationToken);

        foreach (var recipientUserId in visibleRecipientUserIds)
        {
            await CreateUserNotificationAsync(
                recipientUserId,
                UserNotificationTypes.PaymentAssigned,
                $"payment:{payment.Id}:assigned:{recipientUserId}",
                message.Subject,
                message.Body,
                "blue",
                "payment",
                payment.Id,
                null,
                null,
                new
                {
                    paymentId = payment.Id,
                    actorUserId,
                    actorName = normalizedActorName
                },
                cancellationToken,
                objectVisibilityValidated: true,
                resolvedNotificationChannels: recipientNotificationChannels[recipientUserId]);
        }
    }

    public async Task NotifyPaymentChangedAsync(
        Payment payment,
        IReadOnlyList<string> changeLabels,
        string actorName,
        int actorUserId,
        IReadOnlyCollection<int> recipientUserIds,
        CancellationToken cancellationToken = default)
    {
        var normalizedChanges = NormalizePaymentChangeLabels(changeLabels);
        if (payment.Id <= 0 || normalizedChanges.Count == 0 || recipientUserIds.Count == 0)
        {
            return;
        }

        var visibleRecipientUserIds = await ResolveVisiblePaymentNotificationRecipientIdsAsync(
            payment,
            RolePermissionCatalog.Payments,
            recipientUserIds,
            actorUserId,
            cancellationToken);
        if (visibleRecipientUserIds.Count == 0)
        {
            return;
        }

        var normalizedActorName = NormalizeNotificationText(actorName, "Сотрудник");
        var changeText = string.Join('\n', normalizedChanges.Select(label => $"- {label}"));
        var message = await RenderTemplateAsync(
            NotificationTemplateKeys.PaymentChanged,
            $"Изменен платеж: {BuildPaymentTitle(payment)}",
            BuildPaymentChangedBody(payment, normalizedActorName, normalizedChanges),
            BuildPaymentTemplateValues(payment, normalizedActorName, normalizedChanges),
            purpose: NotificationPurposes.ManualTest,
            cancellationToken: cancellationToken);
        var stateHash = BuildStableTextHash(string.Join("|",
        [
            payment.Status.ToString(),
            payment.Date.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            payment.Amount.ToString("0.00", CultureInfo.InvariantCulture),
            (payment.PaymentSourceId ?? 0).ToString(CultureInfo.InvariantCulture),
            (payment.PaymentStatusId ?? 0).ToString(CultureInfo.InvariantCulture),
            string.Join(";", normalizedChanges)
        ]));
        var recipientNotificationChannels = await ResolveUserNotificationChannelsAsync(
            visibleRecipientUserIds,
            UserNotificationTypes.PaymentChanged,
            [
                NotificationChannels.InApp,
                NotificationChannels.Toast,
                NotificationChannels.WebPush,
            ],
            cancellationToken);

        foreach (var recipientUserId in visibleRecipientUserIds)
        {
            await CreateUserNotificationAsync(
                recipientUserId,
                UserNotificationTypes.PaymentChanged,
                $"payment:{payment.Id}:changed:{recipientUserId}:{stateHash}",
                message.Subject,
                message.Body,
                "amber",
                "payment",
                payment.Id,
                null,
                null,
                new
                {
                    paymentId = payment.Id,
                    actorUserId,
                    actorName = normalizedActorName,
                    changes = normalizedChanges,
                    changeText
                },
                cancellationToken,
                objectVisibilityValidated: true,
                resolvedNotificationChannels: recipientNotificationChannels[recipientUserId]);
        }
    }

    public async Task NotifyInvoiceCreatedAsync(
        Payment invoicePayment,
        string actorName,
        int actorUserId,
        IReadOnlyCollection<int> recipientUserIds,
        CancellationToken cancellationToken = default)
    {
        if (invoicePayment.Id <= 0 || recipientUserIds.Count == 0)
        {
            return;
        }

        var visibleRecipientUserIds = await ResolveVisiblePaymentNotificationRecipientIdsAsync(
            invoicePayment,
            RolePermissionCatalog.Accounts,
            recipientUserIds,
            actorUserId,
            cancellationToken);
        if (visibleRecipientUserIds.Count == 0)
        {
            return;
        }

        var normalizedActorName = NormalizeNotificationText(actorName, "Сотрудник");
        var message = await RenderTemplateAsync(
            NotificationTemplateKeys.InvoiceCreated,
            $"Создан счет №{BuildInvoiceNumber(invoicePayment)}",
            BuildInvoiceCreatedBody(invoicePayment, normalizedActorName),
            BuildInvoiceTemplateValues(invoicePayment, normalizedActorName, []),
            purpose: NotificationPurposes.ManualTest,
            cancellationToken: cancellationToken);
        var recipientNotificationChannels = await ResolveUserNotificationChannelsAsync(
            visibleRecipientUserIds,
            UserNotificationTypes.InvoiceCreated,
            [
                NotificationChannels.InApp,
                NotificationChannels.Toast,
                NotificationChannels.WebPush,
            ],
            cancellationToken);

        foreach (var recipientUserId in visibleRecipientUserIds)
        {
            await CreateUserNotificationAsync(
                recipientUserId,
                UserNotificationTypes.InvoiceCreated,
                $"invoice:{invoicePayment.Id}:created:{recipientUserId}",
                message.Subject,
                message.Body,
                "blue",
                "invoice",
                invoicePayment.Id,
                null,
                null,
                new
                {
                    invoiceId = invoicePayment.Id,
                    invoiceNumber = BuildInvoiceNumber(invoicePayment),
                    actorUserId,
                    actorName = normalizedActorName
                },
                cancellationToken,
                objectVisibilityValidated: true,
                resolvedNotificationChannels: recipientNotificationChannels[recipientUserId]);
        }
    }

    public async Task NotifyInvoiceChangedAsync(
        Payment invoicePayment,
        IReadOnlyList<string> changeLabels,
        string actorName,
        int actorUserId,
        IReadOnlyCollection<int> recipientUserIds,
        CancellationToken cancellationToken = default)
    {
        var normalizedChanges = NormalizePaymentChangeLabels(changeLabels);
        if (invoicePayment.Id <= 0 || normalizedChanges.Count == 0 || recipientUserIds.Count == 0)
        {
            return;
        }

        var visibleRecipientUserIds = await ResolveVisiblePaymentNotificationRecipientIdsAsync(
            invoicePayment,
            RolePermissionCatalog.Accounts,
            recipientUserIds,
            actorUserId,
            cancellationToken);
        if (visibleRecipientUserIds.Count == 0)
        {
            return;
        }

        var normalizedActorName = NormalizeNotificationText(actorName, "Сотрудник");
        var message = await RenderTemplateAsync(
            NotificationTemplateKeys.InvoiceChanged,
            $"Изменен счет №{BuildInvoiceNumber(invoicePayment)}",
            BuildInvoiceChangedBody(invoicePayment, normalizedActorName, normalizedChanges),
            BuildInvoiceTemplateValues(invoicePayment, normalizedActorName, normalizedChanges),
            purpose: NotificationPurposes.ManualTest,
            cancellationToken: cancellationToken);
        var stateHash = BuildStableTextHash(string.Join("|",
        [
            invoicePayment.Status.ToString(),
            invoicePayment.InvoiceWorkflowStatus ?? string.Empty,
            invoicePayment.Date.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            invoicePayment.Amount.ToString("0.00", CultureInfo.InvariantCulture),
            invoicePayment.PaidAmount.ToString("0.00", CultureInfo.InvariantCulture),
            string.Join(";", normalizedChanges)
        ]));
        var recipientNotificationChannels = await ResolveUserNotificationChannelsAsync(
            visibleRecipientUserIds,
            UserNotificationTypes.InvoiceChanged,
            [
                NotificationChannels.InApp,
                NotificationChannels.Toast,
                NotificationChannels.WebPush,
            ],
            cancellationToken);

        foreach (var recipientUserId in visibleRecipientUserIds)
        {
            await CreateUserNotificationAsync(
                recipientUserId,
                UserNotificationTypes.InvoiceChanged,
                $"invoice:{invoicePayment.Id}:changed:{recipientUserId}:{stateHash}",
                message.Subject,
                message.Body,
                "amber",
                "invoice",
                invoicePayment.Id,
                null,
                null,
                new
                {
                    invoiceId = invoicePayment.Id,
                    invoiceNumber = BuildInvoiceNumber(invoicePayment),
                    actorUserId,
                    actorName = normalizedActorName,
                    changes = normalizedChanges
                },
                cancellationToken,
                objectVisibilityValidated: true,
                resolvedNotificationChannels: recipientNotificationChannels[recipientUserId]);
        }
    }

    public async Task NotifyActCreatedAsync(
        Act act,
        string actorName,
        int actorUserId,
        IReadOnlyCollection<int> recipientUserIds,
        CancellationToken cancellationToken = default)
    {
        if (act.Id <= 0 || recipientUserIds.Count == 0)
        {
            return;
        }

        var visibleRecipientUserIds = await ResolveVisibleActNotificationRecipientIdsAsync(
            act,
            recipientUserIds,
            actorUserId,
            cancellationToken);
        if (visibleRecipientUserIds.Count == 0)
        {
            return;
        }

        var normalizedActorName = NormalizeNotificationText(actorName, "Сотрудник");
        var message = await RenderTemplateAsync(
            NotificationTemplateKeys.ActCreated,
            $"Создан акт №{BuildActNumber(act)}",
            BuildActCreatedBody(act, normalizedActorName),
            BuildActTemplateValues(act, normalizedActorName, []),
            purpose: NotificationPurposes.ManualTest,
            cancellationToken: cancellationToken);
        var recipientNotificationChannels = await ResolveUserNotificationChannelsAsync(
            visibleRecipientUserIds,
            UserNotificationTypes.ActCreated,
            [
                NotificationChannels.InApp,
                NotificationChannels.Toast,
                NotificationChannels.WebPush,
            ],
            cancellationToken);

        foreach (var recipientUserId in visibleRecipientUserIds)
        {
            await CreateUserNotificationAsync(
                recipientUserId,
                UserNotificationTypes.ActCreated,
                $"act:{act.Id}:created:{recipientUserId}",
                message.Subject,
                message.Body,
                "blue",
                "act",
                act.Id,
                null,
                null,
                new
                {
                    actId = act.Id,
                    actNumber = BuildActNumber(act),
                    actorUserId,
                    actorName = normalizedActorName
                },
                cancellationToken,
                objectVisibilityValidated: true,
                resolvedNotificationChannels: recipientNotificationChannels[recipientUserId]);
        }
    }

    public async Task NotifyActChangedAsync(
        Act act,
        IReadOnlyList<string> changeLabels,
        string actorName,
        int actorUserId,
        IReadOnlyCollection<int> recipientUserIds,
        CancellationToken cancellationToken = default)
    {
        var normalizedChanges = NormalizePaymentChangeLabels(changeLabels);
        if (act.Id <= 0 || normalizedChanges.Count == 0 || recipientUserIds.Count == 0)
        {
            return;
        }

        var visibleRecipientUserIds = await ResolveVisibleActNotificationRecipientIdsAsync(
            act,
            recipientUserIds,
            actorUserId,
            cancellationToken);
        if (visibleRecipientUserIds.Count == 0)
        {
            return;
        }

        var normalizedActorName = NormalizeNotificationText(actorName, "Сотрудник");
        var message = await RenderTemplateAsync(
            NotificationTemplateKeys.ActChanged,
            $"Изменен акт №{BuildActNumber(act)}",
            BuildActChangedBody(act, normalizedActorName, normalizedChanges),
            BuildActTemplateValues(act, normalizedActorName, normalizedChanges),
            purpose: NotificationPurposes.ManualTest,
            cancellationToken: cancellationToken);
        var stateHash = BuildStableTextHash(string.Join("|",
        [
            act.Status.ToString(),
            act.WorkflowStatus ?? string.Empty,
            act.Date.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            act.Amount.ToString("0.00", CultureInfo.InvariantCulture),
            string.Join(";", normalizedChanges)
        ]));
        var recipientNotificationChannels = await ResolveUserNotificationChannelsAsync(
            visibleRecipientUserIds,
            UserNotificationTypes.ActChanged,
            [
                NotificationChannels.InApp,
                NotificationChannels.Toast,
                NotificationChannels.WebPush,
            ],
            cancellationToken);

        foreach (var recipientUserId in visibleRecipientUserIds)
        {
            await CreateUserNotificationAsync(
                recipientUserId,
                UserNotificationTypes.ActChanged,
                $"act:{act.Id}:changed:{recipientUserId}:{stateHash}",
                message.Subject,
                message.Body,
                "amber",
                "act",
                act.Id,
                null,
                null,
                new
                {
                    actId = act.Id,
                    actNumber = BuildActNumber(act),
                    actorUserId,
                    actorName = normalizedActorName,
                    changes = normalizedChanges
                },
                cancellationToken,
                objectVisibilityValidated: true,
                resolvedNotificationChannels: recipientNotificationChannels[recipientUserId]);
        }
    }

    public async Task NotifyContractCreatedAsync(
        Contract contract,
        string actorName,
        int actorUserId,
        IReadOnlyCollection<int> recipientUserIds,
        CancellationToken cancellationToken = default)
    {
        if (contract.Id <= 0 || recipientUserIds.Count == 0)
        {
            return;
        }

        var visibleRecipientUserIds = await ResolveVisibleContractNotificationRecipientIdsAsync(
            contract,
            recipientUserIds,
            actorUserId,
            cancellationToken);
        if (visibleRecipientUserIds.Count == 0)
        {
            return;
        }

        var normalizedActorName = NormalizeNotificationText(actorName, "Сотрудник");
        var message = await RenderTemplateAsync(
            NotificationTemplateKeys.ContractCreated,
            $"Создан договор №{BuildContractNumber(contract)}",
            BuildContractCreatedBody(contract, normalizedActorName),
            BuildContractTemplateValues(contract, normalizedActorName, [], null),
            purpose: NotificationPurposes.ManualTest,
            cancellationToken: cancellationToken);
        var recipientNotificationChannels = await ResolveUserNotificationChannelsAsync(
            visibleRecipientUserIds,
            UserNotificationTypes.ContractCreated,
            [
                NotificationChannels.InApp,
                NotificationChannels.Toast,
                NotificationChannels.WebPush,
            ],
            cancellationToken);

        foreach (var recipientUserId in visibleRecipientUserIds)
        {
            await CreateUserNotificationAsync(
                recipientUserId,
                UserNotificationTypes.ContractCreated,
                $"contract:{contract.Id}:created:{recipientUserId}",
                message.Subject,
                message.Body,
                "blue",
                "contract",
                contract.Id,
                null,
                null,
                new
                {
                    contractId = contract.Id,
                    contractNumber = BuildContractNumber(contract),
                    actorUserId,
                    actorName = normalizedActorName
                },
                cancellationToken,
                objectVisibilityValidated: true,
                resolvedNotificationChannels: recipientNotificationChannels[recipientUserId]);
        }
    }

    public async Task NotifyContractChangedAsync(
        Contract contract,
        IReadOnlyList<string> changeLabels,
        string actorName,
        int actorUserId,
        IReadOnlyCollection<int> recipientUserIds,
        CancellationToken cancellationToken = default)
    {
        var normalizedChanges = NormalizePaymentChangeLabels(changeLabels);
        if (contract.Id <= 0 || normalizedChanges.Count == 0 || recipientUserIds.Count == 0)
        {
            return;
        }

        var visibleRecipientUserIds = await ResolveVisibleContractNotificationRecipientIdsAsync(
            contract,
            recipientUserIds,
            actorUserId,
            cancellationToken);
        if (visibleRecipientUserIds.Count == 0)
        {
            return;
        }

        var normalizedActorName = NormalizeNotificationText(actorName, "Сотрудник");
        var message = await RenderTemplateAsync(
            NotificationTemplateKeys.ContractChanged,
            $"Изменен договор №{BuildContractNumber(contract)}",
            BuildContractChangedBody(contract, normalizedActorName, normalizedChanges),
            BuildContractTemplateValues(contract, normalizedActorName, normalizedChanges, null),
            purpose: NotificationPurposes.ManualTest,
            cancellationToken: cancellationToken);
        var stateHash = BuildStableTextHash(string.Join("|",
        [
            contract.Status ?? string.Empty,
            contract.ValidUntil?.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
            contract.Amount?.ToString("0.00", CultureInfo.InvariantCulture) ?? string.Empty,
            string.Join(";", normalizedChanges)
        ]));
        var recipientNotificationChannels = await ResolveUserNotificationChannelsAsync(
            visibleRecipientUserIds,
            UserNotificationTypes.ContractChanged,
            [
                NotificationChannels.InApp,
                NotificationChannels.Toast,
                NotificationChannels.WebPush,
            ],
            cancellationToken);

        foreach (var recipientUserId in visibleRecipientUserIds)
        {
            await CreateUserNotificationAsync(
                recipientUserId,
                UserNotificationTypes.ContractChanged,
                $"contract:{contract.Id}:changed:{recipientUserId}:{stateHash}",
                message.Subject,
                message.Body,
                "amber",
                "contract",
                contract.Id,
                null,
                null,
                new
                {
                    contractId = contract.Id,
                    contractNumber = BuildContractNumber(contract),
                    actorUserId,
                    actorName = normalizedActorName,
                    changes = normalizedChanges
                },
                cancellationToken,
                objectVisibilityValidated: true,
                resolvedNotificationChannels: recipientNotificationChannels[recipientUserId]);
        }
    }

    public async Task NotifyContractExpiringAsync(
        Contract contract,
        int daysLeft,
        IReadOnlyCollection<int> recipientUserIds,
        CancellationToken cancellationToken = default)
    {
        if (contract.Id <= 0 || !contract.ValidUntil.HasValue || recipientUserIds.Count == 0)
        {
            return;
        }

        var visibleRecipientUserIds = await ResolveVisibleContractNotificationRecipientIdsAsync(
            contract,
            recipientUserIds,
            actorUserId: null,
            cancellationToken);
        if (visibleRecipientUserIds.Count == 0)
        {
            return;
        }

        var message = await RenderTemplateAsync(
            NotificationTemplateKeys.ContractExpiring,
            $"Истекает срок договора №{BuildContractNumber(contract)}",
            BuildContractExpiringBody(contract, daysLeft),
            BuildContractTemplateValues(contract, "CRM", [], daysLeft),
            purpose: NotificationPurposes.ManualTest,
            cancellationToken: cancellationToken);
        var expirationDate = contract.ValidUntil.Value.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var recipientNotificationChannels = await ResolveUserNotificationChannelsAsync(
            visibleRecipientUserIds,
            UserNotificationTypes.ContractExpiring,
            [
                NotificationChannels.InApp,
                NotificationChannels.Toast,
                NotificationChannels.WebPush,
            ],
            cancellationToken);

        foreach (var recipientUserId in visibleRecipientUserIds)
        {
            await CreateUserNotificationAsync(
                recipientUserId,
                UserNotificationTypes.ContractExpiring,
                $"contract:{contract.Id}:expiring:{recipientUserId}:{expirationDate}",
                message.Subject,
                message.Body,
                "rose",
                "contract",
                contract.Id,
                null,
                null,
                new
                {
                    contractId = contract.Id,
                    contractNumber = BuildContractNumber(contract),
                    validUntil = expirationDate,
                    daysLeft
                },
                cancellationToken,
                objectVisibilityValidated: true,
                resolvedNotificationChannels: recipientNotificationChannels[recipientUserId]);
        }
    }

    public Task NotifyCaseCourtEventAsync(
        ClientCase clientCase,
        ClientCaseCourtEvent courtEvent,
        string action,
        string actorName,
        int actorUserId,
        IReadOnlyCollection<int> recipientUserIds,
        CancellationToken cancellationToken = default)
        => NotifyCaseCourtEventCoreAsync(clientCase, courtEvent, action, actorName, actorUserId, recipientUserIds, cancellationToken, null);

    private async Task<IReadOnlyList<NotificationDelivery>> NotifyCaseCourtEventCoreAsync(
        ClientCase clientCase, ClientCaseCourtEvent courtEvent, string action, string actorName, int actorUserId,
        IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken, CaseCourtDeliveryClaim? courtClaim)
    {
        if (clientCase.Id <= 0 || courtEvent.Id <= 0)
        {
            return Array.Empty<NotificationDelivery>();
        }

        var deliveries = new List<NotificationDelivery>();
        var isRescheduled = string.Equals(action, "rescheduled", StringComparison.OrdinalIgnoreCase);
        var notificationType = isRescheduled
            ? UserNotificationTypes.CourtHearing
            : ResolveCourtEventNotificationType(courtEvent);
        var recipients = await IncludeConfiguredCourtLeaderAsync(
            recipientUserIds,
            isRescheduled || notificationType == UserNotificationTypes.CourtDecisionRecorded,
            cancellationToken);
        if (recipients.Count == 0) return Array.Empty<NotificationDelivery>();
        var visibleRecipientUserIds = await ResolveVisibleCaseNotificationRecipientIdsAsync(
            clientCase,
            recipients,
            actorUserId,
            cancellationToken);
        if (visibleRecipientUserIds.Count == 0)
        {
            return Array.Empty<NotificationDelivery>();
        }

        var normalizedActorName = NormalizeNotificationText(actorName, "Сотрудник");
        var templateKey = isRescheduled
            ? NotificationTemplateKeys.CourtHearingRescheduled
            : ResolveCourtEventTemplateKey(notificationType);
        var title = BuildCourtEventSubject(clientCase, courtEvent, notificationType, action);

        var version = (courtEvent.UpdatedAt ?? courtEvent.CreatedAt).Ticks.ToString(CultureInfo.InvariantCulture);
        var allowedChannels = notificationType == UserNotificationTypes.CourtDecisionRecorded
            ? new[] { NotificationChannels.InApp, NotificationChannels.Toast, NotificationChannels.ForcedModal, NotificationChannels.WebPush, NotificationChannels.Email }
            : new[] { NotificationChannels.InApp, NotificationChannels.Toast, NotificationChannels.ForcedModal, NotificationChannels.WebPush };
        var recipientNotificationChannels = await ResolveUserNotificationChannelsAsync(
            visibleRecipientUserIds,
            notificationType,
            allowedChannels,
            cancellationToken);
        var recipientTimeZones = await CalendarTimeZoneHelper.ResolveUserTimeZonesAsync(
            _context,
            visibleRecipientUserIds,
            cancellationToken);
        var emailRecipientIds = visibleRecipientUserIds
            .Where(id => recipientNotificationChannels[id].Contains(NotificationChannels.Email))
            .ToArray();
        var recipientsById = await ResolveRecipientsAsync(emailRecipientIds, cancellationToken);

        foreach (var recipientUserId in visibleRecipientUserIds)
        {
            var recipientTimeZone = recipientTimeZones.GetValueOrDefault(
                recipientUserId,
                CalendarTimeZoneHelper.ResolveTimeZone(null));
            var body = BuildCourtEventBody(
                clientCase,
                courtEvent,
                action,
                normalizedActorName,
                notificationType,
                recipientTimeZone);
            var message = await RenderTemplateAsync(
                templateKey,
                title,
                body,
                BuildCourtEventTemplateValues(clientCase, courtEvent, action, normalizedActorName, recipientTimeZone),
                purpose: NotificationPurposes.TaskReminder,
                cancellationToken: cancellationToken);
            var eventKey = courtClaim?.EventKey ?? $"case:{clientCase.Id}:court-event:{courtEvent.Id}:{notificationType}:{version}:{recipientUserId}";
            await CreateUserNotificationAsync(
                recipientUserId,
                notificationType,
                eventKey,
                message.Subject,
                message.Body,
                ResolveCourtEventTone(notificationType),
                "case",
                clientCase.Id,
                null,
                null,
                new
                {
                    clientCaseId = clientCase.Id,
                    courtEventId = courtEvent.Id,
                    action = NormalizeCourtEventAction(action),
                    actorUserId,
                    actorName = normalizedActorName,
                    eventType = courtEvent.EventType,
                    movementStatus = courtEvent.MovementStatus,
                    scheduledAt = courtEvent.ScheduledAt,
                    courtName = courtEvent.CourtName,
                    courtCaseNumber = courtEvent.CourtCaseNumber,
                    clientCaseDocumentId = courtEvent.ClientCaseDocumentId,
                    proceduralDeadlineId = courtEvent.ProceduralDeadlineId
                },
                cancellationToken,
                objectVisibilityValidated: courtClaim is null,
                resolvedNotificationChannels: recipientNotificationChannels[recipientUserId],
                courtClaim: courtClaim);
            if (recipientsById.TryGetValue(recipientUserId, out var recipient))
            {
                deliveries.AddRange(courtClaim is null
                    ? await SendAsync(recipient, message, [NotificationChannels.Email], eventKey, cancellationToken)
                    : await SendCourtEmailForEventAsync(clientCase, recipient, message, notificationType, courtClaim, cancellationToken));
            }
        }
        return deliveries;
    }

    public async Task NotifyCaseHearingDateChangedAsync(
        ClientCase clientCase,
        DateTime? previousAt,
        DateTime currentAt,
        string actorName,
        int actorUserId,
        IReadOnlyCollection<int> recipientUserIds,
        DateTime changedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (clientCase.Id <= 0 || previousAt == currentAt)
        {
            return;
        }

        var recipients = await IncludeConfiguredCourtLeaderAsync(recipientUserIds, previousAt.HasValue, cancellationToken);
        if (recipients.Count == 0) return;
        var visibleRecipientUserIds = await ResolveVisibleCaseNotificationRecipientIdsAsync(
            clientCase,
            recipients,
            actorUserId,
            cancellationToken);
        if (visibleRecipientUserIds.Count == 0)
        {
            return;
        }

        var channelsByUserId = await ResolveUserNotificationChannelsAsync(
            visibleRecipientUserIds,
            UserNotificationTypes.CourtHearing,
            [NotificationChannels.InApp, NotificationChannels.Toast, NotificationChannels.ForcedModal, NotificationChannels.WebPush],
            cancellationToken);
        var timeZonesByUserId = await CalendarTimeZoneHelper.ResolveUserTimeZonesAsync(
            _context,
            visibleRecipientUserIds,
            cancellationToken);
        var normalizedActorName = NormalizeNotificationText(actorName, "Сотрудник");
        var isRescheduled = previousAt.HasValue;
        var title = isRescheduled
            ? $"Перенесено судебное заседание: {BuildClientCaseTitle(clientCase)}"
            : $"Назначено судебное заседание: {BuildClientCaseTitle(clientCase)}";

        foreach (var recipientUserId in visibleRecipientUserIds)
        {
            var timeZone = timeZonesByUserId.GetValueOrDefault(
                recipientUserId,
                CalendarTimeZoneHelper.ResolveTimeZone(null));
            var body = (isRescheduled
                    ? $"{normalizedActorName} изменил(а) дату ближайшего заседания. Проверьте подготовку по делу.\n\n"
                    : $"{normalizedActorName} назначил(а) ближайшее судебное заседание. Проверьте подготовку по делу.\n\n")
                + $"Дело: {BuildClientCaseTitle(clientCase)}\n"
                + (previousAt.HasValue
                    ? $"Прежняя дата: {FormatCourtEventDate(previousAt.Value, allDay: false, recipientTimeZone: timeZone)}\n"
                    : string.Empty)
                + $"Новая дата: {FormatCourtEventDate(currentAt, allDay: false, recipientTimeZone: timeZone)}";
            await CreateUserNotificationAsync(
                recipientUserId,
                UserNotificationTypes.CourtHearing,
                $"case:{clientCase.Id}:hearing-date-changed:{changedAtUtc.Ticks}:{recipientUserId}",
                title,
                body,
                "cyan",
                "case",
                clientCase.Id,
                null,
                null,
                new { clientCaseId = clientCase.Id, previousAt, currentAt, actorUserId, actorName = normalizedActorName },
                cancellationToken,
                objectVisibilityValidated: true,
                resolvedNotificationChannels: channelsByUserId[recipientUserId]);
        }
    }

    public async Task NotifyCaseDecisionDateChangedAsync(
        ClientCase clientCase,
        DateTime? previousAt,
        DateTime? currentAt,
        string actorName,
        int actorUserId,
        IReadOnlyCollection<int> recipientUserIds,
        DateTime changedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (clientCase.Id <= 0 || previousAt?.Date == currentAt?.Date)
        {
            return;
        }

        var recipients = await IncludeConfiguredCourtLeaderAsync(recipientUserIds, currentAt.HasValue, cancellationToken);
        if (recipients.Count == 0) return;
        var visibleRecipientUserIds = await ResolveVisibleCaseNotificationRecipientIdsAsync(
            clientCase,
            recipients,
            actorUserId,
            cancellationToken);
        if (visibleRecipientUserIds.Count == 0)
        {
            return;
        }

        var channelsByUserId = await ResolveUserNotificationChannelsAsync(
            visibleRecipientUserIds,
            UserNotificationTypes.CourtDecisionRecorded,
            [NotificationChannels.InApp, NotificationChannels.Toast, NotificationChannels.WebPush, NotificationChannels.Email],
            cancellationToken);
        var normalizedActorName = NormalizeNotificationText(actorName, "Сотрудник");
        var title = !currentAt.HasValue
            ? $"Удалена дата судебного решения: {BuildClientCaseTitle(clientCase)}"
            : previousAt.HasValue
                ? $"Изменена дата судебного решения: {BuildClientCaseTitle(clientCase)}"
                : $"Указана дата судебного решения: {BuildClientCaseTitle(clientCase)}";
        var body = $"{normalizedActorName} {(!currentAt.HasValue ? "удалил(а) дату решения" : previousAt.HasValue ? "изменил(а) дату решения" : "указал(а) дату решения")} по делу.\n\n"
            + $"Дело: {BuildClientCaseTitle(clientCase)}\n"
            + (previousAt.HasValue ? $"Прежняя дата: {FormatCourtEventDate(previousAt.Value, allDay: true)}\n" : string.Empty)
            + (currentAt.HasValue ? $"Новая дата: {FormatCourtEventDate(currentAt.Value, allDay: true)}" : "Дата решения пока не указана.");
        var emailRecipientIds = visibleRecipientUserIds
            .Where(id => channelsByUserId[id].Contains(NotificationChannels.Email))
            .ToArray();
        var recipientsById = await ResolveRecipientsAsync(emailRecipientIds, cancellationToken);
        var message = new NotificationMessage(title, body, Purpose: NotificationPurposes.TaskReminder);

        foreach (var recipientUserId in visibleRecipientUserIds)
        {
            var eventKey = $"case:{clientCase.Id}:decision-date-changed:{changedAtUtc.Ticks}:{recipientUserId}";
            await CreateUserNotificationAsync(
                recipientUserId,
                UserNotificationTypes.CourtDecisionRecorded,
                eventKey,
                title,
                body,
                "emerald",
                "case",
                clientCase.Id,
                null,
                null,
                new { clientCaseId = clientCase.Id, previousAt, currentAt, actorUserId, actorName = normalizedActorName },
                cancellationToken,
                objectVisibilityValidated: true,
                resolvedNotificationChannels: channelsByUserId[recipientUserId]);
            if (recipientsById.TryGetValue(recipientUserId, out var recipient))
            {
                await SendAsync(recipient, message, [NotificationChannels.Email], eventKey, cancellationToken);
            }
        }
    }

    public Task NotifyCaseProceduralDeadlineAsync(
        ClientCase clientCase,
        ClientCaseProceduralDeadline deadline,
        string action,
        string actorName,
        int actorUserId,
        IReadOnlyCollection<int> recipientUserIds,
        CancellationToken cancellationToken = default)
        => NotifyCaseProceduralDeadlineCoreAsync(clientCase, deadline, action, actorName, actorUserId, recipientUserIds, cancellationToken, null);

    private async Task<IReadOnlyList<NotificationDelivery>> NotifyCaseProceduralDeadlineCoreAsync(
        ClientCase clientCase, ClientCaseProceduralDeadline deadline, string action, string actorName, int actorUserId,
        IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken, CaseCourtDeliveryClaim? courtClaim)
    {
        if (clientCase.Id <= 0 || deadline.Id <= 0 || recipientUserIds.Count == 0)
        {
            return Array.Empty<NotificationDelivery>();
        }

        var visibleRecipientUserIds = await ResolveVisibleCaseNotificationRecipientIdsAsync(
            clientCase,
            recipientUserIds,
            actorUserId,
            cancellationToken);
        if (visibleRecipientUserIds.Count == 0)
        {
            return Array.Empty<NotificationDelivery>();
        }

        var normalizedAction = NormalizeCaseProceduralDeadlineAction(action);
        var normalizedActorName = NormalizeNotificationText(actorName, "CRM");
        var actionText = ResolveCaseProceduralDeadlineActionText(normalizedAction);
        var now = DateTime.UtcNow;
        var eventVersion = (deadline.UpdatedAt ?? deadline.ClosedAt ?? deadline.CreatedAt)
            .Ticks.ToString(CultureInfo.InvariantCulture);
        var timeZonesByUserId = await CalendarTimeZoneHelper.ResolveUserTimeZonesAsync(
            _context, visibleRecipientUserIds, cancellationToken);
        var templateDefinition = NotificationTemplateCatalog.Find(NotificationTemplateKeys.CaseProceduralDeadline);
        var templateStorage = templateDefinition is null
            ? new NotificationTemplateStorage()
            : await LoadTemplateStorageAsync(cancellationToken);
        var messagesByTimeZoneId = new Dictionary<string, NotificationMessage>(StringComparer.Ordinal);
        var recipientNotificationChannels = await ResolveUserNotificationChannelsAsync(
            visibleRecipientUserIds,
            UserNotificationTypes.CaseProceduralDeadline,
            [
                NotificationChannels.InApp,
                NotificationChannels.Toast,
                NotificationChannels.WebPush,
            ],
            cancellationToken);

        foreach (var recipientUserId in visibleRecipientUserIds)
        {
            var recipientTimeZone = timeZonesByUserId[recipientUserId];
            if (!messagesByTimeZoneId.TryGetValue(recipientTimeZone.Id, out var message))
            {
                message = RenderTemplate(
                    templateDefinition,
                    templateStorage,
                    BuildCaseProceduralDeadlineSubject(clientCase, deadline, normalizedAction),
                    BuildCaseProceduralDeadlineBody(clientCase, deadline, normalizedAction, normalizedActorName, recipientTimeZone),
                    BuildCaseProceduralDeadlineTemplateValues(clientCase, deadline, normalizedAction, normalizedActorName, recipientTimeZone),
                    isSensitive: false,
                    purpose: NotificationPurposes.TaskReminder);
                messagesByTimeZoneId[recipientTimeZone.Id] = message;
            }
            var version = string.Equals(normalizedAction, "reminder", StringComparison.OrdinalIgnoreCase)
                ? CaseDeadlineReminderCalendar.LocalDay(now, recipientTimeZone)
                    .ToString("yyyyMMdd", CultureInfo.InvariantCulture)
                : eventVersion;
            await CreateUserNotificationAsync(
                recipientUserId,
                UserNotificationTypes.CaseProceduralDeadline,
                courtClaim?.EventKey ?? $"case:{clientCase.Id}:procedural-deadline:{deadline.Id}:{normalizedAction}:{version}:{recipientUserId}",
                message.Subject,
                message.Body,
                ResolveCaseProceduralDeadlineTone(normalizedAction, deadline),
                "case",
                clientCase.Id,
                null,
                null,
                new
                {
                    clientCaseId = clientCase.Id,
                    proceduralDeadlineId = deadline.Id,
                    action = normalizedAction,
                    actionText,
                    actorUserId,
                    actorName = normalizedActorName,
                    deadlineType = deadline.DeadlineType,
                    dueAt = deadline.DueAt,
                    allDay = deadline.AllDay,
                    status = deadline.Status,
                    warningDaysBefore = deadline.WarningDaysBefore,
                    taskJournalItemId = deadline.TaskJournalItemId,
                    clientCaseDocumentId = deadline.ClientCaseDocumentId,
                    sourceType = deadline.SourceType,
                    sourceId = deadline.SourceId
                },
                cancellationToken,
                objectVisibilityValidated: courtClaim is null,
                resolvedNotificationChannels: recipientNotificationChannels[recipientUserId],
                courtClaim: courtClaim);
        }
        return Array.Empty<NotificationDelivery>();
    }

    /// <summary>
    /// Создаёт суточные напоминания о процессуальных сроках одной порцией: настройки
    /// каналов, шаблон и существующие ключи читаются пакетно, а новые записи сохраняются
    /// одной операцией независимо от количества дел и получателей.
    /// </summary>
    public async Task<int> NotifyCaseProceduralDeadlineRemindersAsync(
        IReadOnlyCollection<CaseProceduralDeadlineReminderBatchItem> reminders,
        CancellationToken cancellationToken = default)
    {
        var createdAt = DateTime.UtcNow;
        var recipientCandidates = reminders
            .Where(item => item.ClientCase.Id > 0 && item.Deadline.Id > 0)
            .SelectMany(item => item.RecipientUserIds
                .Where(userId => userId > 0)
                .Distinct()
                .Select(userId => new
                {
                    Item = item,
                    RecipientUserId = userId,
                }))
            .ToArray();
        if (recipientCandidates.Length == 0)
        {
            return 0;
        }

        var recipientIds = recipientCandidates
            .Select(item => item.RecipientUserId)
            .Distinct()
            .ToArray();
        var timeZonesByUserId = await CalendarTimeZoneHelper.ResolveUserTimeZonesAsync(
            _context, recipientIds, cancellationToken);
        var candidates = recipientCandidates
            .Select(item => new
            {
                item.Item,
                item.RecipientUserId,
                EventKey = BuildCaseProceduralDeadlineReminderEventKey(
                    item.Item.ClientCase.Id,
                    item.Item.Deadline.Id,
                    item.RecipientUserId,
                    CaseDeadlineReminderCalendar.LocalDay(
                        createdAt, timeZonesByUserId[item.RecipientUserId])
                        .ToString("yyyyMMdd", CultureInfo.InvariantCulture)),
            })
            .GroupBy(item => item.EventKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var channelsByUser = await ResolveUserNotificationChannelsAsync(
            recipientIds,
            UserNotificationTypes.CaseProceduralDeadline,
            [NotificationChannels.InApp, NotificationChannels.Toast, NotificationChannels.WebPush],
            cancellationToken);
        var candidateEventKeys = candidates.Select(candidate => candidate.EventKey).ToArray();
        var existingEventKeys = await _context.UserNotifications
            .AsNoTracking()
            .Where(item => candidateEventKeys.Contains(item.EventKey))
            .Select(item => item.EventKey)
            .ToHashSetAsync(cancellationToken);
        var templateDefinition = NotificationTemplateCatalog.Find(NotificationTemplateKeys.CaseProceduralDeadline);
        var templateStorage = templateDefinition is null
            ? new NotificationTemplateStorage()
            : await LoadTemplateStorageAsync(cancellationToken);
        var messagesByDeadlineAndTimeZone = new Dictionary<(int DeadlineId, string TimeZoneId), NotificationMessage>();
        var notifications = new List<UserNotification>(candidates.Length);
        var deliveryChannels = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);

        foreach (var candidate in candidates)
        {
            var channels = channelsByUser[candidate.RecipientUserId];
            if (!channels.Contains(NotificationChannels.InApp)
                || existingEventKeys.Contains(candidate.EventKey))
            {
                continue;
            }

            var deadline = candidate.Item.Deadline;
            var clientCase = candidate.Item.ClientCase;
            var recipientTimeZone = timeZonesByUserId[candidate.RecipientUserId];
            var messageKey = (deadline.Id, recipientTimeZone.Id);
            if (!messagesByDeadlineAndTimeZone.TryGetValue(messageKey, out var message))
            {
                message = RenderTemplate(
                    templateDefinition,
                    templateStorage,
                    BuildCaseProceduralDeadlineSubject(clientCase, deadline, "reminder"),
                    BuildCaseProceduralDeadlineBody(clientCase, deadline, "reminder", "CRM", recipientTimeZone),
                    BuildCaseProceduralDeadlineTemplateValues(clientCase, deadline, "reminder", "CRM", recipientTimeZone),
                    isSensitive: false,
                    purpose: NotificationPurposes.TaskReminder);
                messagesByDeadlineAndTimeZone[messageKey] = message;
            }

            var notification = new UserNotification
            {
                RecipientUserId = candidate.RecipientUserId,
                Type = UserNotificationTypes.CaseProceduralDeadline,
                EventKey = candidate.EventKey,
                Title = Truncate(message.Subject.Trim(), 300),
                BodyPreview = Truncate(message.Body.Trim(), 1000),
                Tone = ResolveCaseProceduralDeadlineTone("reminder", deadline),
                ObjectType = "case",
                ObjectId = clientCase.Id,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    clientCaseId = clientCase.Id,
                    proceduralDeadlineId = deadline.Id,
                    action = "reminder",
                    actionText = ResolveCaseProceduralDeadlineActionText("reminder"),
                    actorUserId = 0,
                    actorName = "CRM",
                    deadlineType = deadline.DeadlineType,
                    dueAt = deadline.DueAt,
                    allDay = deadline.AllDay,
                    status = deadline.Status,
                    warningDaysBefore = deadline.WarningDaysBefore,
                    taskJournalItemId = deadline.TaskJournalItemId,
                    clientCaseDocumentId = deadline.ClientCaseDocumentId,
                    sourceType = deadline.SourceType,
                    sourceId = deadline.SourceId,
                }, JsonOptions),
                CreatedAt = createdAt,
            };
            notifications.Add(notification);
            deliveryChannels[candidate.EventKey] = channels;
        }

        if (notifications.Count == 0)
        {
            return 0;
        }

        _context.UserNotifications.AddRange(notifications);
        await _context.SaveChangesAsync(cancellationToken);
        foreach (var notification in notifications)
        {
            await QueueMaxNotificationIfAvailableAsync(notification, cancellationToken);
            var channels = deliveryChannels[notification.EventKey];
            await _realtimePublisher.PublishUserNotificationAsync(
                notification,
                channels.Contains(NotificationChannels.Toast),
                cancellationToken);
            if (_webPushNotificationService is not null
                && channels.Contains(NotificationChannels.WebPush))
            {
                await _webPushNotificationService.SendUserNotificationAsync(notification, cancellationToken);
            }
        }

        return notifications.Count;
    }

    private static string BuildCaseProceduralDeadlineReminderEventKey(
        int clientCaseId,
        int deadlineId,
        int recipientUserId,
        string reminderVersion)
        => $"case:{clientCaseId}:procedural-deadline:{deadlineId}:reminder:{reminderVersion}:{recipientUserId}";

    public async Task NotifyCaseConversationAsync(
        ClientCase clientCase,
        string messageText,
        int attachmentCount,
        string actorName,
        int actorUserId,
        IReadOnlyCollection<int> recipientUserIds,
        DateTime occurredAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (clientCase.Id <= 0 || recipientUserIds.Count == 0)
        {
            return;
        }

        var visibleRecipientUserIds = await ResolveVisibleCaseNotificationRecipientIdsAsync(
            clientCase,
            recipientUserIds,
            actorUserId,
            cancellationToken);
        if (visibleRecipientUserIds.Count == 0)
        {
            return;
        }

        var normalizedActorName = NormalizeNotificationText(actorName, "Сотрудник");
        var normalizedMessageText = NormalizeNotificationText(messageText, "Новое сообщение");
        var normalizedOccurredAt = NormalizeUtcInstant(occurredAtUtc);
        var message = await RenderTemplateAsync(
            NotificationTemplateKeys.CaseConversation,
            BuildCaseConversationSubject(clientCase, attachmentCount),
            BuildCaseConversationBody(clientCase, normalizedMessageText, attachmentCount, normalizedActorName, normalizedOccurredAt),
            BuildCaseConversationTemplateValues(clientCase, normalizedMessageText, attachmentCount, normalizedActorName, normalizedOccurredAt),
            purpose: NotificationPurposes.TaskReminder,
            cancellationToken: cancellationToken);
        var stateHash = BuildStableTextHash(string.Join("|",
            clientCase.Id.ToString(CultureInfo.InvariantCulture),
            normalizedMessageText,
            Math.Max(attachmentCount, 0).ToString(CultureInfo.InvariantCulture),
            normalizedActorName,
            normalizedOccurredAt.Ticks.ToString(CultureInfo.InvariantCulture)));
        var recipientNotificationChannels = await ResolveUserNotificationChannelsAsync(
            visibleRecipientUserIds,
            UserNotificationTypes.CaseConversation,
            [
                NotificationChannels.InApp,
                NotificationChannels.Toast,
                NotificationChannels.WebPush,
            ],
            cancellationToken);

        foreach (var recipientUserId in visibleRecipientUserIds)
        {
            await CreateUserNotificationAsync(
                recipientUserId,
                UserNotificationTypes.CaseConversation,
                $"case:{clientCase.Id}:conversation:{stateHash}:{recipientUserId}",
                message.Subject,
                message.Body,
                attachmentCount > 0 ? "emerald" : "blue",
                "case",
                clientCase.Id,
                null,
                null,
                new
                {
                    clientCaseId = clientCase.Id,
                    attachmentCount = Math.Max(attachmentCount, 0),
                    hasAttachments = attachmentCount > 0,
                    actorUserId,
                    actorName = normalizedActorName,
                    messagePreview = Truncate(normalizedMessageText, 300),
                    occurredAt = normalizedOccurredAt
                },
                cancellationToken,
                objectVisibilityValidated: true,
                resolvedNotificationChannels: recipientNotificationChannels[recipientUserId]);
        }
    }

    public async Task NotifyCaseDocumentAsync(
        ClientCase clientCase,
        ClientCaseDocument document,
        string action,
        string actorName,
        int actorUserId,
        IReadOnlyCollection<int> recipientUserIds,
        CancellationToken cancellationToken = default)
    {
        if (clientCase.Id <= 0 || document.Id <= 0 || recipientUserIds.Count == 0)
        {
            return;
        }

        var visibleRecipientUserIds = await ResolveVisibleCaseNotificationRecipientIdsAsync(
            clientCase,
            recipientUserIds,
            actorUserId,
            cancellationToken);
        if (visibleRecipientUserIds.Count == 0)
        {
            return;
        }

        var normalizedAction = NormalizeCaseDocumentAction(action);
        var normalizedActorName = NormalizeNotificationText(actorName, "CRM");
        var requiresAction = IsCaseDocumentActionRequired(document);
        var message = await RenderTemplateAsync(
            NotificationTemplateKeys.CaseDocument,
            BuildCaseDocumentSubject(clientCase, document, normalizedAction, requiresAction),
            BuildCaseDocumentBody(clientCase, document, normalizedAction, normalizedActorName, requiresAction),
            BuildCaseDocumentTemplateValues(clientCase, document, normalizedAction, normalizedActorName, requiresAction),
            purpose: NotificationPurposes.TaskReminder,
            cancellationToken: cancellationToken);
        var stateHash = BuildStableTextHash(string.Join("|",
            document.DocumentKey,
            document.Version.ToString(CultureInfo.InvariantCulture),
            document.DocumentType,
            document.Status,
            document.OriginalFileName,
            document.UploadedAt.Ticks.ToString(CultureInfo.InvariantCulture),
            normalizedAction));
        var recipientNotificationChannels = await ResolveUserNotificationChannelsAsync(
            visibleRecipientUserIds,
            UserNotificationTypes.CaseDocument,
            [
                NotificationChannels.InApp,
                NotificationChannels.Toast,
                NotificationChannels.WebPush,
            ],
            cancellationToken);

        foreach (var recipientUserId in visibleRecipientUserIds)
        {
            await CreateUserNotificationAsync(
                recipientUserId,
                UserNotificationTypes.CaseDocument,
                $"case:{clientCase.Id}:document:{document.Id}:{normalizedAction}:{stateHash}:{recipientUserId}",
                message.Subject,
                message.Body,
                ResolveCaseDocumentTone(normalizedAction, requiresAction),
                "case",
                clientCase.Id,
                null,
                null,
                new
                {
                    clientCaseId = clientCase.Id,
                    clientCaseDocumentId = document.Id,
                    action = normalizedAction,
                    actorUserId,
                    actorName = normalizedActorName,
                    documentKey = document.DocumentKey,
                    version = document.Version,
                    documentType = document.DocumentType,
                    status = document.Status,
                    fileName = document.OriginalFileName,
                    contentType = document.ContentType,
                    sizeBytes = document.SizeBytes,
                    requiresAction
                },
                cancellationToken,
                objectVisibilityValidated: true,
                resolvedNotificationChannels: recipientNotificationChannels[recipientUserId]);
        }
    }

    public async Task NotifyCaseAccessRequestedAsync(
        ClientCase clientCase,
        string requesterName,
        int requesterUserId,
        string? reason,
        DateTime requestedAt,
        IReadOnlyCollection<int> managerUserIds,
        CancellationToken cancellationToken = default)
    {
        if (clientCase.Id <= 0 || requesterUserId <= 0 || managerUserIds.Count == 0)
        {
            return;
        }

        var recipients = managerUserIds
            .Where(userId => userId > 0 && userId != requesterUserId)
            .Distinct()
            .ToList();
        if (recipients.Count == 0)
        {
            return;
        }

        var normalizedRequesterName = NormalizeNotificationText(requesterName, "Сотрудник");
        var normalizedReason = NormalizeNotificationText(reason, "не указана");
        var requestedAtUtc = NormalizeUtcInstant(requestedAt);
        var message = await RenderTemplateAsync(
            NotificationTemplateKeys.CaseAccessRequested,
            $"Запрошен доступ к делу: {BuildClientCaseTitle(clientCase)}",
            BuildCaseAccessRequestBody(clientCase, normalizedRequesterName, normalizedReason, requestedAtUtc),
            BuildCaseAccessTemplateValues(clientCase, normalizedRequesterName, normalizedReason, null, "запрошен", requestedAtUtc, null),
            purpose: NotificationPurposes.TaskReminder,
            cancellationToken: cancellationToken);
        var recipientNotificationChannels = await ResolveUserNotificationChannelsAsync(
            recipients,
            UserNotificationTypes.CaseAccessRequested,
            [
                NotificationChannels.InApp,
                NotificationChannels.Toast,
                NotificationChannels.WebPush,
            ],
            cancellationToken);

        foreach (var managerUserId in recipients)
        {
            await CreateUserNotificationAsync(
                managerUserId,
                UserNotificationTypes.CaseAccessRequested,
                $"case:{clientCase.Id}:access-request:{requesterUserId}:{requestedAtUtc.Ticks}:{managerUserId}",
                message.Subject,
                message.Body,
                "violet",
                "case",
                clientCase.Id,
                null,
                null,
                new
                {
                    clientCaseId = clientCase.Id,
                    requestedByUserId = requesterUserId,
                    requestedByUserName = normalizedRequesterName,
                    reason = normalizedReason,
                    requestedAt = requestedAtUtc
                },
                cancellationToken,
                resolvedNotificationChannels: recipientNotificationChannels[managerUserId]);
        }
    }

    public async Task NotifyCaseAccessResolvedAsync(
        ClientCase clientCase,
        bool granted,
        string actorName,
        int actorUserId,
        int requestedByUserId,
        string requestedByUserName,
        string? requestReason,
        string? comment,
        DateTime resolvedAt,
        CancellationToken cancellationToken = default)
    {
        if (clientCase.Id <= 0
            || requestedByUserId <= 0
            || UserNotificationAntiSpamPolicy.ShouldSuppressSelfNotification(requestedByUserId, actorUserId))
        {
            return;
        }

        var normalizedActorName = NormalizeNotificationText(actorName, "Сотрудник");
        var normalizedRequesterName = NormalizeNotificationText(requestedByUserName, "Сотрудник");
        var normalizedReason = NormalizeNotificationText(requestReason, "не указана");
        var normalizedComment = NormalizeNotificationText(comment, "нет");
        var resolvedAtUtc = NormalizeUtcInstant(resolvedAt);
        var decisionText = granted ? "разрешен" : "отклонен";
        var message = await RenderTemplateAsync(
            NotificationTemplateKeys.CaseAccessResolved,
            granted
                ? $"Доступ к делу разрешен: {BuildClientCaseTitle(clientCase)}"
                : $"Доступ к делу отклонен: {BuildClientCaseTitle(clientCase)}",
            BuildCaseAccessResolutionBody(clientCase, granted, normalizedActorName, normalizedComment, resolvedAtUtc),
            BuildCaseAccessTemplateValues(clientCase, normalizedRequesterName, normalizedReason, normalizedComment, decisionText, null, resolvedAtUtc, normalizedActorName),
            purpose: NotificationPurposes.TaskReminder,
            cancellationToken: cancellationToken);

        await CreateUserNotificationAsync(
            requestedByUserId,
            UserNotificationTypes.CaseAccessResolved,
            $"case:{clientCase.Id}:access-request:resolved:{(granted ? "granted" : "denied")}:{resolvedAtUtc.Ticks}:{requestedByUserId}",
            message.Subject,
            message.Body,
            granted ? "emerald" : "orange",
            "case",
            clientCase.Id,
            null,
            null,
            new
            {
                clientCaseId = clientCase.Id,
                requestedByUserId,
                requestedByUserName = normalizedRequesterName,
                requestReason = normalizedReason,
                resolvedByUserId = actorUserId,
                resolvedByUserName = normalizedActorName,
                resolvedAt = resolvedAtUtc,
                granted,
                comment = normalizedComment
            },
            cancellationToken);
    }

    public async Task NotifyCourtSyncFailedAsync(
        ClientCase clientCase,
        string providerName,
        string summary,
        string actorName,
        int actorUserId,
        IReadOnlyCollection<int> recipientUserIds,
        CancellationToken cancellationToken = default)
    {
        if (clientCase.Id <= 0 || recipientUserIds.Count == 0)
        {
            return;
        }

        var visibleRecipientUserIds = await ResolveVisibleCaseNotificationRecipientIdsAsync(
            clientCase,
            recipientUserIds,
            actorUserId,
            cancellationToken);
        if (visibleRecipientUserIds.Count == 0)
        {
            return;
        }

        var normalizedActorName = NormalizeNotificationText(actorName, "CRM");
        var normalizedSummary = NormalizeNotificationText(summary, "Судебный источник временно недоступен.");
        var provider = NormalizeNotificationText(providerName, "судебный источник");
        var message = await RenderTemplateAsync(
            NotificationTemplateKeys.CourtSyncFailed,
            $"Не удалось проверить судебный источник: {BuildClientCaseTitle(clientCase)}",
            BuildCourtSyncFailedBody(clientCase, provider, normalizedSummary),
            BuildCourtSyncFailedTemplateValues(clientCase, provider, normalizedSummary, normalizedActorName),
            purpose: NotificationPurposes.TaskReminder,
            cancellationToken: cancellationToken);
        var stateHash = BuildStableTextHash($"{provider}|{normalizedSummary}");
        var recipientNotificationChannels = await ResolveUserNotificationChannelsAsync(
            visibleRecipientUserIds,
            UserNotificationTypes.CourtSyncFailed,
            [
                NotificationChannels.InApp,
                NotificationChannels.Toast,
                NotificationChannels.WebPush,
            ],
            cancellationToken);

        foreach (var recipientUserId in visibleRecipientUserIds)
        {
            await CreateUserNotificationAsync(
                recipientUserId,
                UserNotificationTypes.CourtSyncFailed,
                $"case:{clientCase.Id}:court-sync-failed:{recipientUserId}:{DateTime.UtcNow:yyyyMMdd}:{stateHash}",
                message.Subject,
                message.Body,
                "rose",
                "case",
                clientCase.Id,
                null,
                null,
                new
                {
                    clientCaseId = clientCase.Id,
                    actorUserId,
                    actorName = normalizedActorName,
                    providerName = provider,
                    summary = normalizedSummary
                },
                cancellationToken,
                objectVisibilityValidated: true,
                resolvedNotificationChannels: recipientNotificationChannels[recipientUserId]);
        }
    }

    public async Task NotifyCourtSyncStalledAsync(
        ClientCase clientCase,
        string providerName,
        CancellationToken cancellationToken = default)
    {
        if (clientCase.Id <= 0
            || !clientCase.CourtSyncMonitoringEnabled
            || clientCase.CourtSyncFailureStartedAt is not { } failureStartedAt
            || failureStartedAt > DateTime.UtcNow.AddHours(-24))
        {
            return;
        }

        var adminIds = await _context.Users
            .AsNoTracking()
            .Where(user => user.IsActive && user.IsApproved && user.Role != null && user.Role.Name == "admin")
            .Select(user => user.Id)
            .ToListAsync(cancellationToken);
        var visibleAdminIds = await ResolveVisibleCaseNotificationRecipientIdsAsync(
            clientCase,
            adminIds,
            actorUserId: null,
            cancellationToken);
        if (visibleAdminIds.Count == 0)
        {
            return;
        }

        var provider = NormalizeNotificationText(providerName, "судебный источник");
        var title = $"Синхронизация с судом не работает более суток: {BuildClientCaseTitle(clientCase)}";
        var body = $"Не удаётся обновить данные дела из источника «{provider}» с {failureStartedAt:dd.MM.yyyy HH:mm} UTC.\n"
            + $"Последняя ошибка: {NormalizeNotificationText(clientCase.CourtSyncLastError, "Источник недоступен.")}\n"
            + "Проверьте подключение и данные дела в CRM. После успешного обновления отсчёт сбоя начнётся заново.";
        var message = new NotificationMessage(title, body, Purpose: NotificationPurposes.TaskReminder);
        var eventKey = $"case:{clientCase.Id}:court-sync-stalled:{failureStartedAt.Ticks}";
        var channelsByUserId = await ResolveUserNotificationChannelsAsync(
            visibleAdminIds,
            UserNotificationTypes.SystemAdminWarning,
            [NotificationChannels.InApp, NotificationChannels.Toast, NotificationChannels.WebPush, NotificationChannels.Email],
            cancellationToken);
        var emailRecipientIds = visibleAdminIds
            .Where(id => channelsByUserId[id].Contains(NotificationChannels.Email))
            .ToArray();
        var recipientsById = await ResolveRecipientsAsync(emailRecipientIds, cancellationToken);

        foreach (var adminId in visibleAdminIds)
        {
            await CreateUserNotificationAsync(
                adminId,
                UserNotificationTypes.SystemAdminWarning,
                eventKey,
                title,
                body,
                "rose",
                "case",
                clientCase.Id,
                null,
                null,
                new { clientCaseId = clientCase.Id, providerName = provider, failureStartedAt },
                cancellationToken,
                objectVisibilityValidated: true,
                resolvedNotificationChannels: channelsByUserId[adminId]);

            if (recipientsById.TryGetValue(adminId, out var recipient))
            {
                await SendAsync(recipient, message, [NotificationChannels.Email], eventKey, cancellationToken);
            }
        }
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
        var caseInactivitySubject = $"Автоконтроль дела: {task.ObjectName}";
        var recipientTimeZones = await CalendarTimeZoneHelper.ResolveUserTimeZonesAsync(
            _context,
            recipientUserIds,
            cancellationToken);
        var recipientNotificationChannels = await ResolveUserNotificationChannelsAsync(
            recipientUserIds,
            UserNotificationTypes.CaseInactivityControl,
            channels.Concat([
                NotificationChannels.InApp,
                NotificationChannels.Toast,
                NotificationChannels.WebPush,
            ]),
            cancellationToken, task: task);
        var templateDefinition = NotificationTemplateCatalog.Find(NotificationTemplateKeys.CaseInactivityControl);
        var templateStorage = templateDefinition is null
            ? new NotificationTemplateStorage()
            : await LoadTemplateStorageAsync(cancellationToken);
        var deliveryRecipientIds = recipientUserIds
            .Where(userId => channels.Any(recipientNotificationChannels[userId].Contains))
            .ToList();
        var deliveryRecipients = await ResolveRecipientsAsync(deliveryRecipientIds, cancellationToken);
        var deliveries = new List<NotificationDelivery>();
        foreach (var recipientUserId in recipientUserIds)
        {
            var recipientTimeZone = recipientTimeZones[recipientUserId];
            var caseInactivityBody = BuildCaseInactivityControlBody(task, daysWithoutActiveTask, recipientTimeZone);
            var message = RenderTemplate(
                templateDefinition,
                templateStorage,
                caseInactivitySubject,
                caseInactivityBody,
                NotificationTemplateCatalog.MergeValues(
                    BuildTaskTemplateValues(task, recipientTimeZone),
                    new Dictionary<string, string?> { ["case.daysWithoutActiveTask"] = daysWithoutActiveTask.ToString(CultureInfo.InvariantCulture) }),
                isSensitive: false,
                purpose: NotificationPurposes.TaskReminder);

            if (HasTaskChannel(task, NotificationChannels.InApp))
            {
                var metadata = new
                {
                    task.ClientCaseId,
                    daysWithoutActiveTask,
                    task.AssigneeUserId,
                    isEscalationObserver = recipientUserId != task.AssigneeUserId.Value
                };
                if (task.ClientCaseId.HasValue)
                {
                    await CreateUserNotificationAsync(
                        recipientUserId,
                        UserNotificationTypes.CaseInactivityControl,
                        eventKey,
                        message.Subject,
                        message.Body,
                        "amber",
                        "case",
                        task.ClientCaseId,
                        task.Id,
                        task,
                        metadata,
                        cancellationToken,
                        resolvedNotificationChannels: recipientNotificationChannels[recipientUserId]);
                }
                else
                {
                    await CreateUserNotificationAsync(
                        recipientUserId,
                        UserNotificationTypes.CaseInactivityControl,
                        eventKey,
                        message.Subject,
                        message.Body,
                        "amber",
                        task,
                        metadata,
                        cancellationToken,
                        recipientNotificationChannels[recipientUserId]);
                }
            }

            var enabledChannels = channels
                .Where(recipientNotificationChannels[recipientUserId].Contains)
                .ToList();
            if (enabledChannels.Count > 0
                && deliveryRecipients.TryGetValue(recipientUserId, out var recipient))
            {
                deliveries.AddRange(await SendAsync(recipient, message, enabledChannels, eventKey, cancellationToken));
            }
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
        var conflictSubject = $"Конфликт расписания: {task.Title}";
        var conflictKey = string.Join('-', effectiveConflicts.Select(item => item.Id).OrderBy(id => id));
        var eventKey = $"task:{task.Id}:schedule-conflict:{task.DueAt:yyyyMMddHHmm}:{task.PlannedDurationMinutes}:{conflictKey}";
        var recipientTimeZones = await CalendarTimeZoneHelper.ResolveUserTimeZonesAsync(
            _context,
            recipients,
            cancellationToken);
        var recipientNotificationChannels = await ResolveUserNotificationChannelsAsync(
            recipients,
            UserNotificationTypes.TaskScheduleConflict,
            channels.Concat([
                NotificationChannels.InApp,
                NotificationChannels.Toast,
                NotificationChannels.WebPush,
            ]),
            cancellationToken, task: task);
        var templateDefinition = NotificationTemplateCatalog.Find(NotificationTemplateKeys.TaskScheduleConflict);
        var templateStorage = templateDefinition is null
            ? new NotificationTemplateStorage()
            : await LoadTemplateStorageAsync(cancellationToken);
        var deliveryRecipientIds = recipients
            .Where(userId => channels.Any(recipientNotificationChannels[userId].Contains))
            .ToList();
        var deliveryRecipients = await ResolveRecipientsAsync(deliveryRecipientIds, cancellationToken);
        var deliveries = new List<NotificationDelivery>();

        foreach (var userId in recipients)
        {
            var recipientTimeZone = recipientTimeZones[userId];
            var conflictBody = BuildTaskScheduleConflictBody(task, effectiveConflicts, actorName, recipientTimeZone);
            var message = RenderTemplate(
                templateDefinition,
                templateStorage,
                conflictSubject,
                conflictBody,
                NotificationTemplateCatalog.MergeValues(
                    BuildTaskTemplateValues(task, recipientTimeZone, actorName),
                    new Dictionary<string, string?> { ["conflicts"] = BuildConflictSummary(effectiveConflicts, recipientTimeZone) }),
                isSensitive: false,
                purpose: NotificationPurposes.TaskReminder);

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
                    cancellationToken,
                    recipientNotificationChannels[userId]);
            }

            var enabledChannels = channels
                .Where(recipientNotificationChannels[userId].Contains)
                .ToList();
            if (enabledChannels.Count > 0 && deliveryRecipients.TryGetValue(userId, out var recipient))
            {
                deliveries.AddRange(await SendAsync(recipient, message, enabledChannels, eventKey, cancellationToken));
            }
        }

        return deliveries;
    }

    public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskApprovalRequestedAsync(
        TaskJournalItem task,
        string actorName,
        int actorUserId,
        CancellationToken cancellationToken = default)
    {
        var approver = GetCurrentApprover(task);
        return NotifyApprovalForEventCoreAsync(task, actorName, actorUserId,
            $"task:{task.Id}:approval-request:{approver?.Id ?? 0}:{DateTime.UtcNow:yyyyMMddHHmmssfff}", false, cancellationToken);
    }

    public Task<IReadOnlyList<NotificationDelivery>> NotifyApprovalForEventAsync(
        TaskJournalItem task, string actorName, int actorUserId, string eventKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventKey);
        return NotifyApprovalForEventCoreAsync(task, actorName, actorUserId, eventKey, true, cancellationToken);
    }

    private async Task<IReadOnlyList<NotificationDelivery>> NotifyApprovalForEventCoreAsync(
        TaskJournalItem task, string actorName, int actorUserId, string eventKey, bool retryMaxQueue, CancellationToken cancellationToken)
    {
        var recipients = ResolveApprovalRequestRecipientUserIds(task, actorUserId);
        if (retryMaxQueue)
        {
            var eligible = await ResolveDurableTaskRecipientsAsync(task.Id, recipients, cancellationToken);
            recipients = recipients.Where(eligible.Contains).ToArray();
        }
        if (recipients.Count == 0)
        {
            return Array.Empty<NotificationDelivery>();
        }

        var currentApprover = GetCurrentApprover(task);
        if (retryMaxQueue && currentApprover is null)
            return Array.Empty<NotificationDelivery>();
        var approvalSubject = $"Задача ожидает согласования: {task.Title}";
        var channels = ResolveTaskMessageChannels(task);
        var recipientTimeZones = await CalendarTimeZoneHelper.ResolveUserTimeZonesAsync(
            _context,
            recipients,
            cancellationToken);
        var recipientNotificationChannels = await ResolveUserNotificationChannelsAsync(
            recipients,
            UserNotificationTypes.TaskApprovalRequested,
            channels.Concat([
                NotificationChannels.InApp,
                NotificationChannels.Toast,
                NotificationChannels.WebPush,
            ]),
            cancellationToken, task: task);
        var templateDefinition = NotificationTemplateCatalog.Find(NotificationTemplateKeys.TaskApprovalRequested);
        var templateStorage = templateDefinition is null
            ? new NotificationTemplateStorage()
            : await LoadTemplateStorageAsync(cancellationToken);
        var deliveryRecipientIds = recipients
            .Where(userId => channels.Any(recipientNotificationChannels[userId].Contains))
            .ToList();
        var deliveryRecipients = await ResolveRecipientsAsync(deliveryRecipientIds, cancellationToken);
        var deliveries = new List<NotificationDelivery>();
        foreach (var userId in recipients)
        {
            var recipientTimeZone = recipientTimeZones[userId];
            var approvalBody = BuildTaskApprovalRequestedBody(task, actorName, currentApprover, recipientTimeZone);
            var message = RenderTemplate(
                templateDefinition,
                templateStorage,
                approvalSubject,
                approvalBody,
                NotificationTemplateCatalog.MergeValues(
                    BuildTaskTemplateValues(task, recipientTimeZone, actorName),
                    new Dictionary<string, string?> { ["approval.approverName"] = currentApprover?.User?.FullName ?? currentApprover?.User?.Email ?? "не указан" }),
                isSensitive: false,
                purpose: NotificationPurposes.TaskReminder);

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
                    cancellationToken,
                    recipientNotificationChannels[userId],
                    retryMaxQueue: retryMaxQueue,
                    replayRealtimeOnRetry: retryMaxQueue,
                    expectedApprovalParticipantId: retryMaxQueue ? currentApprover!.Id : null);
            }

            var enabledChannels = channels
                .Where(recipientNotificationChannels[userId].Contains)
                .ToList();
            if (retryMaxQueue && !await IsDurableTaskRecipientCurrentAsync(task, userId, false, cancellationToken, currentApprover!.Id))
                continue;
            if (enabledChannels.Count > 0 && deliveryRecipients.TryGetValue(userId, out var recipient))
            {
                deliveries.AddRange(await SendAsync(recipient, message, enabledChannels, eventKey, cancellationToken));
            }
        }

        return deliveries;
    }

    private async Task<HashSet<int>> ResolveDurableTaskRecipientsAsync(
        int taskId, IReadOnlyCollection<int> candidates, CancellationToken cancellationToken, bool authorOnly = false)
    {
        if (candidates.Count == 0) return [];
        var task = await _context.TaskJournalItems.AsNoTracking().Where(item => item.Id == taskId)
            .Select(item => new { item.SalesRequestId }).SingleOrDefaultAsync(cancellationToken);
        if (task is null) return [];

        var eligible = _context.Users.AsNoTracking().Where(user => candidates.Contains(user.Id)
            && user.IsActive && user.IsApproved && user.Role != null
            && (user.Role.Name.ToLower() == "admin" || user.Role.Permissions.Any(permission =>
                permission.Section == RolePermissionCatalog.Tasks && permission.CanView))
            && _context.TaskJournalItems.Any(current => current.Id == taskId
                && current.SalesRequestId == task.SalesRequestId
                && (current.AuthorUserId == user.Id || (!authorOnly && (current.AssigneeUserId == user.Id
                    || current.Participants.Any(participant => participant.UserId == user.Id))))));
        if (task.SalesRequestId is int requestId)
            eligible = eligible.WithSalesRequestAccess(requestId, _context);
        return (await eligible.Select(user => user.Id).ToListAsync(cancellationToken)).ToHashSet();
    }

    private async Task<bool> IsDurableTaskRecipientCurrentAsync(TaskJournalItem task, int userId, bool authorOnly, CancellationToken ct, int? expectedApprovalParticipantId = null)
    {
        var eligible = await ResolveDurableTaskRecipientsAsync(task.Id, [userId], ct, authorOnly);
        return eligible.Contains(userId) && await _context.TaskJournalItems.AsNoTracking()
            .AnyAsync(current => current.Id == task.Id && current.StatusTransitionToken == task.StatusTransitionToken, ct)
            && (!expectedApprovalParticipantId.HasValue || await IsApprovalStageCurrentAsync(task.Id, expectedApprovalParticipantId.Value, ct));
    }

    private async Task<bool> IsApprovalStageCurrentAsync(int taskId, int expectedParticipantId, CancellationToken ct)
    {
        var current = await _context.TaskJournalParticipants.AsNoTracking()
            .Where(participant => participant.TaskJournalItemId == taskId
                && participant.Role.ToLower() == "approver" && participant.ApprovalStatus.ToLower() != "approved")
            .OrderBy(participant => participant.SortOrder).ThenBy(participant => participant.Id)
            .Select(participant => (int?)participant.Id).FirstOrDefaultAsync(ct);
        return current == expectedParticipantId;
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
        var isCompletedApproval = !isRevision && string.Equals(task.Status, "done", StringComparison.OrdinalIgnoreCase);
        var type = isRevision ? UserNotificationTypes.TaskRevisionRequested : UserNotificationTypes.TaskApproved;
        var templateKey = isRevision ? NotificationTemplateKeys.TaskRevisionRequested : NotificationTemplateKeys.TaskApproved;
        var tone = isRevision ? "orange" : "emerald";
        var subject = isRevision
            ? $"Задача возвращена на доработку: {task.Title}"
            : isCompletedApproval
                ? $"Задача согласована и завершена: {task.Title}"
            : $"Задача согласована: {task.Title}";
        var recipients = ResolveApprovalDecisionRecipientUserIds(task, actorUserId);
        if (recipients.Count == 0)
        {
            return Array.Empty<NotificationDelivery>();
        }

        var channels = ResolveTaskMessageChannels(task);
        var eventKey = $"task:{task.Id}:approval-decision:{approver.Id}:{decision}:{DateTime.UtcNow:yyyyMMddHHmmssfff}";
        var recipientTimeZones = await CalendarTimeZoneHelper.ResolveUserTimeZonesAsync(
            _context,
            recipients,
            cancellationToken);
        var recipientNotificationChannels = await ResolveUserNotificationChannelsAsync(
            recipients,
            type,
            channels.Concat([
                NotificationChannels.InApp,
                NotificationChannels.Toast,
                NotificationChannels.WebPush,
            ]),
            cancellationToken, task: task);
        var templateDefinition = NotificationTemplateCatalog.Find(templateKey);
        var templateStorage = templateDefinition is null
            ? new NotificationTemplateStorage()
            : await LoadTemplateStorageAsync(cancellationToken);
        var deliveryRecipientIds = recipients
            .Where(userId => channels.Any(recipientNotificationChannels[userId].Contains))
            .ToList();
        var deliveryRecipients = await ResolveRecipientsAsync(deliveryRecipientIds, cancellationToken);
        var deliveries = new List<NotificationDelivery>();
        foreach (var userId in recipients)
        {
            var recipientTimeZone = recipientTimeZones[userId];
            var approvalDecisionBody = BuildTaskApprovalDecisionBody(task, approver, decision, actorName, resolution, recipientTimeZone);
            var message = RenderTemplate(
                templateDefinition,
                templateStorage,
                subject,
                approvalDecisionBody,
                NotificationTemplateCatalog.MergeValues(
                    BuildTaskTemplateValues(task, recipientTimeZone, actorName),
                    new Dictionary<string, string?> { ["approval.resolution"] = string.IsNullOrWhiteSpace(resolution) ? "Без комментария." : resolution.Trim() }),
                isSensitive: false,
                purpose: NotificationPurposes.TaskReminder);

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
                    new
                    {
                        actorUserId,
                        actorName,
                        approverId = approver.Id,
                        decision,
                        resolution,
                        resultingStatus = task.Status,
                        completed = isCompletedApproval
                    },
                    cancellationToken,
                    recipientNotificationChannels[userId]);
            }

            var enabledChannels = channels
                .Where(recipientNotificationChannels[userId].Contains)
                .ToList();
            if (enabledChannels.Count > 0 && deliveryRecipients.TryGetValue(userId, out var recipient))
            {
                deliveries.AddRange(await SendAsync(recipient, message, enabledChannels, eventKey, cancellationToken));
            }
        }

        return deliveries;
    }

    public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskDeadlineChangeRequestedAsync(
        TaskJournalItem task,
        TaskJournalDeadlineChangeRequest request,
        CancellationToken cancellationToken = default)
    {
        var actorUserId = request.RequestedByUserId ?? 0;
        var recipients = ResolveDeadlineChangeRequestRecipientUserIds(task, request, actorUserId, includeReviewer: true, includeRequester: false, includeObservers: true);
        var eventKey = $"task:{task.Id}:deadline-change-request:{request.Id}:requested";
        return NotifyTaskDeadlineChangeRequestedCoreAsync(task, request, recipients, eventKey, cancellationToken);
    }

    public async Task<IReadOnlyList<NotificationDelivery>> NotifyTaskDeadlineReviewerAssignedAsync(
        TaskJournalItem task,
        TaskJournalDeadlineChangeRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.ReviewerUserId is not > 0) return Array.Empty<NotificationDelivery>();
        var reviewerId = request.ReviewerUserId.Value;
        IReadOnlyList<int> recipients = [reviewerId];
        var access = await _context.ResolveSectionAccessAsync(CreateNotificationRecipientPrincipal(reviewerId), RolePermissionCatalog.Tasks, cancellationToken, requireApprovedUser: true);
        if (access?.Permission?.CanView != true
            || !await _context.TaskJournalItems.AsNoTracking().Where(item => item.Id == task.Id).ApplyTaskVisibility(access.Visibility, _context).AnyAsync(cancellationToken)
            || !await _context.TaskJournalDeadlineChangeRequests.AsNoTracking().AnyAsync(item => item.Id == request.Id && item.TaskJournalItemId == task.Id && item.Status == "pending" && item.ReviewerUserId == reviewerId && item.UpdatedAt == request.UpdatedAt, cancellationToken))
            return Array.Empty<NotificationDelivery>();

        var eventKey = $"task:{task.Id}:deadline-change-request:{request.Id}:reviewer:{reviewerId}:{request.UpdatedAt?.Ticks ?? 0}";
        return await NotifyTaskDeadlineChangeRequestedCoreAsync(task, request, recipients, eventKey, cancellationToken, retryMaxQueue: true);
    }

    private async Task<IReadOnlyList<NotificationDelivery>> NotifyTaskDeadlineChangeRequestedCoreAsync(
        TaskJournalItem task,
        TaskJournalDeadlineChangeRequest request,
        IReadOnlyList<int> recipients,
        string eventKey,
        CancellationToken cancellationToken,
        bool retryMaxQueue = false)
    {
        if (recipients.Count == 0)
        {
            return Array.Empty<NotificationDelivery>();
        }

        var channels = ResolveTaskMessageChannels(task);
        var recipientTimeZones = await CalendarTimeZoneHelper.ResolveUserTimeZonesAsync(
            _context,
            recipients,
            cancellationToken);
        var recipientNotificationChannels = await ResolveUserNotificationChannelsAsync(
            recipients,
            UserNotificationTypes.TaskDeadlineChangeRequested,
            channels.Concat([
                NotificationChannels.InApp,
                NotificationChannels.Toast,
                NotificationChannels.WebPush,
            ]),
            cancellationToken, task: task);
        var templateDefinition = NotificationTemplateCatalog.Find(NotificationTemplateKeys.TaskChanged);
        var templateStorage = templateDefinition is null
            ? new NotificationTemplateStorage()
            : await LoadTemplateStorageAsync(cancellationToken);
        var deliveryRecipientIds = recipients
            .Where(userId => channels.Any(recipientNotificationChannels[userId].Contains))
            .ToList();
        var deliveryRecipients = await ResolveRecipientsAsync(deliveryRecipientIds, cancellationToken);
        var deliveries = new List<NotificationDelivery>();
        foreach (var userId in recipients)
        {
            var recipientTimeZone = recipientTimeZones[userId];
            var subject = $"Запрос переноса срока: {task.Title}";
            var body = BuildTaskDeadlineChangeRequestBody(task, request, recipientTimeZone);
            var message = RenderTemplate(
                templateDefinition,
                templateStorage,
                subject,
                body,
                NotificationTemplateCatalog.MergeValues(
                    BuildTaskTemplateValues(task, recipientTimeZone, request.RequestedByName),
                    new Dictionary<string, string?>
                    {
                        ["task.changeSubject"] = subject,
                        ["task.changes"] = $"- Запрошен перенос срока на {FormatTaskDueAt(request.RequestedDueAt, recipientTimeZone)}",
                        ["task.commentBlock"] = $"Причина: {request.Reason.Trim()}",
                    }),
                isSensitive: false,
                purpose: NotificationPurposes.TaskReminder);

            if (HasTaskChannel(task, NotificationChannels.InApp))
            {
                await CreateUserNotificationAsync(
                    userId,
                    UserNotificationTypes.TaskDeadlineChangeRequested,
                    eventKey,
                    message.Subject,
                    message.Body,
                    "violet",
                    task,
                    new
                    {
                        deadlineChangeRequestId = request.Id,
                        request.RequestedByUserId,
                        request.RequestedByName,
                        request.ReviewerUserId,
                        request.ReviewerName,
                        currentDueAt = request.CurrentDueAt,
                        requestedDueAt = request.RequestedDueAt,
                        reason = request.Reason
                    },
                    cancellationToken,
                    recipientNotificationChannels[userId],
                    retryMaxQueue);
            }

            var enabledChannels = channels
                .Where(recipientNotificationChannels[userId].Contains)
                .ToList();
            if (enabledChannels.Count > 0 && deliveryRecipients.TryGetValue(userId, out var recipient))
            {
                deliveries.AddRange(await SendAsync(recipient, message, enabledChannels, eventKey, cancellationToken));
            }
        }

        return deliveries;
    }

    public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskDeadlineChangeApprovedAsync(
        TaskJournalItem task,
        TaskJournalDeadlineChangeRequest request,
        CancellationToken cancellationToken = default)
        => NotifyTaskDeadlineChangeDecisionAsync(
            task,
            request,
            UserNotificationTypes.TaskDeadlineChangeApproved,
            $"Перенос срока согласован: {task.Title}",
            "согласовал(а) перенос срока",
            "emerald",
            cancellationToken);

    public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskDeadlineChangeRejectedAsync(
        TaskJournalItem task,
        TaskJournalDeadlineChangeRequest request,
        CancellationToken cancellationToken = default)
        => NotifyTaskDeadlineChangeDecisionAsync(
            task,
            request,
            UserNotificationTypes.TaskDeadlineChangeRejected,
            $"Перенос срока отклонен: {task.Title}",
            "отказал(а) в переносе срока",
            "orange",
            cancellationToken);

    private async Task<IReadOnlyList<NotificationDelivery>> NotifyTaskDeadlineChangeDecisionAsync(
        TaskJournalItem task,
        TaskJournalDeadlineChangeRequest request,
        string type,
        string subject,
        string actionText,
        string tone,
        CancellationToken cancellationToken)
    {
        var actorUserId = request.DecidedByUserId ?? 0;
        var recipients = ResolveDeadlineChangeRequestRecipientUserIds(task, request, actorUserId, includeReviewer: true, includeRequester: true, includeObservers: true);
        if (recipients.Count == 0)
        {
            return Array.Empty<NotificationDelivery>();
        }

        var channels = ResolveTaskMessageChannels(task);
        var statusSegment = string.Equals(type, UserNotificationTypes.TaskDeadlineChangeApproved, StringComparison.OrdinalIgnoreCase)
            ? "approved"
            : "rejected";
        var eventKey = $"task:{task.Id}:deadline-change-request:{request.Id}:{statusSegment}";
        var actorName = string.IsNullOrWhiteSpace(request.DecidedByName) ? request.ReviewerName : request.DecidedByName.Trim();
        var recipientTimeZones = await CalendarTimeZoneHelper.ResolveUserTimeZonesAsync(
            _context,
            recipients,
            cancellationToken);
        var recipientNotificationChannels = await ResolveUserNotificationChannelsAsync(
            recipients,
            type,
            channels.Concat([
                NotificationChannels.InApp,
                NotificationChannels.Toast,
                NotificationChannels.WebPush,
            ]),
            cancellationToken, task: task);
        var templateDefinition = NotificationTemplateCatalog.Find(NotificationTemplateKeys.TaskChanged);
        var templateStorage = templateDefinition is null
            ? new NotificationTemplateStorage()
            : await LoadTemplateStorageAsync(cancellationToken);
        var deliveryRecipientIds = recipients
            .Where(userId => channels.Any(recipientNotificationChannels[userId].Contains))
            .ToList();
        var deliveryRecipients = await ResolveRecipientsAsync(deliveryRecipientIds, cancellationToken);
        var deliveries = new List<NotificationDelivery>();

        foreach (var userId in recipients)
        {
            var recipientTimeZone = recipientTimeZones[userId];
            var body = BuildTaskDeadlineChangeDecisionBody(task, request, actionText, actorName, recipientTimeZone);
            var decisionDueAtText = request.DecisionDueAt.HasValue
                ? FormatTaskDueAt(request.DecisionDueAt.Value, recipientTimeZone)
                : FormatTaskDueAt(task.DueAt, recipientTimeZone);
            var message = RenderTemplate(
                templateDefinition,
                templateStorage,
                subject,
                body,
                NotificationTemplateCatalog.MergeValues(
                    BuildTaskTemplateValues(task, recipientTimeZone, actorName),
                    new Dictionary<string, string?>
                    {
                        ["task.changeSubject"] = subject,
                        ["task.changes"] = string.Equals(statusSegment, "approved", StringComparison.OrdinalIgnoreCase)
                            ? $"- Перенос срока согласован на {decisionDueAtText}"
                            : "- Запрос переноса срока отклонен",
                        ["task.commentBlock"] = string.IsNullOrWhiteSpace(request.DecisionComment)
                            ? string.Empty
                            : $"Комментарий: {request.DecisionComment.Trim()}",
                    }),
                isSensitive: false,
                purpose: NotificationPurposes.TaskReminder);

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
                    new
                    {
                        deadlineChangeRequestId = request.Id,
                        request.RequestedByUserId,
                        request.RequestedByName,
                        request.ReviewerUserId,
                        request.ReviewerName,
                        request.DecidedByUserId,
                        request.DecidedByName,
                        request.RequestedDueAt,
                        request.DecisionDueAt,
                        request.DecisionComment,
                        request.Status
                    },
                    cancellationToken,
                    recipientNotificationChannels[userId]);
            }

            var enabledChannels = channels
                .Where(recipientNotificationChannels[userId].Contains)
                .ToList();
            if (enabledChannels.Count > 0 && deliveryRecipients.TryGetValue(userId, out var recipient))
            {
                deliveries.AddRange(await SendAsync(recipient, message, enabledChannels, eventKey, cancellationToken));
            }
        }

        return deliveries;
    }

    public async Task<IReadOnlyList<NotificationDelivery>> NotifyTaskApprovalOverdueAsync(
        TaskJournalItem task,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (!task.ApprovalDueAt.HasValue)
        {
            return Array.Empty<NotificationDelivery>();
        }

        var currentApprover = GetCurrentApprover(task);
        if (currentApprover is null)
        {
            return Array.Empty<NotificationDelivery>();
        }

        var recipients = ResolveApprovalRequestRecipientUserIds(task, actorUserId: 0);
        if (recipients.Count == 0)
        {
            return Array.Empty<NotificationDelivery>();
        }

        var subject = $"Просрочено согласование: {task.Title}";
        var channels = ResolveTaskMessageChannels(task);
        var recipientTimeZones = await CalendarTimeZoneHelper.ResolveUserTimeZonesAsync(
            _context,
            recipients,
            cancellationToken);
        var recipientNotificationChannels = await ResolveUserNotificationChannelsAsync(
            recipients,
            UserNotificationTypes.TaskApprovalOverdue,
            channels.Concat([
                NotificationChannels.InApp,
                NotificationChannels.Toast,
                NotificationChannels.WebPush,
            ]),
            cancellationToken, task: task);
        var templateDefinition = NotificationTemplateCatalog.Find(NotificationTemplateKeys.TaskApprovalOverdue);
        var templateStorage = templateDefinition is null
            ? new NotificationTemplateStorage()
            : await LoadTemplateStorageAsync(cancellationToken);
        var deliveryRecipientIds = recipients
            .Where(userId => channels.Any(recipientNotificationChannels[userId].Contains))
            .ToList();
        var deliveryRecipients = await ResolveRecipientsAsync(deliveryRecipientIds, cancellationToken);
        var deliveries = new List<NotificationDelivery>();
        foreach (var userId in recipients)
        {
            var recipientTimeZone = recipientTimeZones[userId];
            var localDate = CalendarTimeZoneHelper.ToUserLocal(nowUtc, recipientTimeZone).Date;
            var eventKey = $"task:{task.Id}:approval-overdue:{localDate:yyyyMMdd}";
            var approvalBody = BuildTaskApprovalOverdueBody(task, currentApprover, recipientTimeZone);
            var message = RenderTemplate(
                templateDefinition,
                templateStorage,
                subject,
                approvalBody,
                NotificationTemplateCatalog.MergeValues(
                    BuildTaskTemplateValues(task, recipientTimeZone, "Система"),
                    new Dictionary<string, string?>
                    {
                        ["approval.approverName"] = ResolveParticipantName(currentApprover),
                        ["approval.dueAt"] = FormatTaskDueAt(DateTime.SpecifyKind(task.ApprovalDueAt.Value, DateTimeKind.Utc), recipientTimeZone)
                    }),
                isSensitive: false,
                purpose: NotificationPurposes.TaskReminder);

            if (HasTaskChannel(task, NotificationChannels.InApp))
            {
                await CreateUserNotificationAsync(
                    userId,
                    UserNotificationTypes.TaskApprovalOverdue,
                    eventKey,
                    message.Subject,
                    message.Body,
                    "rose",
                    task,
                    new
                    {
                        currentApproverId = currentApprover.Id,
                        currentApproverUserId = currentApprover.UserId,
                        approvalDueAt = task.ApprovalDueAt
                    },
                    cancellationToken,
                    recipientNotificationChannels[userId]);
            }

            var enabledChannels = channels
                .Where(recipientNotificationChannels[userId].Contains)
                .ToList();
            if (enabledChannels.Count > 0 && deliveryRecipients.TryGetValue(userId, out var recipient))
            {
                deliveries.AddRange(await SendAsync(recipient, message, enabledChannels, eventKey, cancellationToken));
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
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<int>? additionalRecipientUserIds = null)
    {
        var channels = ResolveTaskMessageChannels(task);
        var recipients = ResolveTaskNotificationUserIds(task, actorUserId)
            .Concat(additionalRecipientUserIds ?? Array.Empty<int>())
            .Where(userId => userId > 0 && userId != actorUserId)
            .Distinct()
            .ToList();
        if (recipients.Count == 0)
        {
            return Array.Empty<NotificationDelivery>();
        }

        var hasDueAtChange = HasTaskDueAtChange(changeLabels);
        var taskChangedSubject = hasDueAtChange
            ? "Изменён срок задачи"
            : "Задача изменена";
        var eventKey = hasDueAtChange
            ? $"task:{task.Id}:due-at:{DateTime.SpecifyKind(task.DueAt, DateTimeKind.Utc):yyyyMMddHHmm}:{actorUserId}:{(task.UpdatedAt ?? task.CreatedAt).Ticks}"
            : $"task:{task.Id}:changed:{DateTime.UtcNow:yyyyMMddHHmmssfff}:{actorUserId}";
        var tone = hasDueAtChange ? ResolveDueAtChangeTone(task) : "blue";
        var recipientTimeZones = await CalendarTimeZoneHelper.ResolveUserTimeZonesAsync(
            _context,
            recipients,
            cancellationToken);
        var recipientNotificationChannels = await ResolveUserNotificationChannelsAsync(
            recipients,
            UserNotificationTypes.TaskChanged,
            channels.Concat([
                NotificationChannels.InApp,
                NotificationChannels.Toast,
                NotificationChannels.WebPush,
            ]),
            cancellationToken, task: task);
        var templateDefinition = NotificationTemplateCatalog.Find(NotificationTemplateKeys.TaskChanged);
        var templateStorage = templateDefinition is null
            ? new NotificationTemplateStorage()
            : await LoadTemplateStorageAsync(cancellationToken);
        var deliveryRecipientIds = recipients
            .Where(userId => channels.Any(recipientNotificationChannels[userId].Contains))
            .ToList();
        var deliveryRecipients = await ResolveRecipientsAsync(deliveryRecipientIds, cancellationToken);
        var deliveries = new List<NotificationDelivery>();
        foreach (var userId in recipients)
        {
            var recipientTimeZone = recipientTimeZones[userId];
            var recipientChangeLabels = FormatTaskChangeLabels(changeLabels, recipientTimeZone);
            var taskChangedBody = BuildTaskChangedBody(task, recipientChangeLabels, actorName, comment, recipientTimeZone);
            var message = RenderTemplate(
                templateDefinition,
                templateStorage,
                taskChangedSubject,
                taskChangedBody,
                NotificationTemplateCatalog.MergeValues(
                    BuildTaskTemplateValues(task, recipientTimeZone, actorName),
                    new Dictionary<string, string?>
                    {
                        ["task.changes"] = string.Join(Environment.NewLine, OrderTaskChangeLabels(recipientChangeLabels).Select(label => $"- {label}")),
                        ["task.commentBlock"] = string.IsNullOrWhiteSpace(comment) ? string.Empty : $"Комментарий: {comment.Trim()}",
                        ["task.changeSubject"] = taskChangedSubject,
                    }),
                isSensitive: false,
                purpose: NotificationPurposes.TaskReminder);

            if (HasTaskChannel(task, NotificationChannels.InApp))
            {
                await CreateUserNotificationAsync(
                    userId,
                    UserNotificationTypes.TaskChanged,
                    eventKey,
                    message.Subject,
                    message.Body,
                    tone,
                    task,
                    new { actorUserId, actorName, changeLabels, hasDueAtChange },
                    cancellationToken,
                    recipientNotificationChannels[userId]);
            }

            var enabledChannels = channels
                .Where(recipientNotificationChannels[userId].Contains)
                .ToList();
            if (enabledChannels.Count > 0 && deliveryRecipients.TryGetValue(userId, out var recipient))
            {
                deliveries.AddRange(await SendAsync(recipient, message, enabledChannels, eventKey, cancellationToken));
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

        var taskCommentSubject = $"Комментарий по задаче: {task.Title}";
        var eventKey = $"task:{task.Id}:comment:{comment.Id}";
        var recipientTimeZones = await CalendarTimeZoneHelper.ResolveUserTimeZonesAsync(
            _context,
            recipients,
            cancellationToken);
        var recipientNotificationChannels = await ResolveUserNotificationChannelsAsync(
            recipients,
            UserNotificationTypes.TaskComment,
            channels.Concat([
                NotificationChannels.InApp,
                NotificationChannels.Toast,
                NotificationChannels.WebPush,
            ]),
            cancellationToken, task: task);
        var templateDefinition = NotificationTemplateCatalog.Find(NotificationTemplateKeys.TaskComment);
        var templateStorage = templateDefinition is null
            ? new NotificationTemplateStorage()
            : await LoadTemplateStorageAsync(cancellationToken);
        var deliveryRecipientIds = recipients
            .Where(userId => channels.Any(recipientNotificationChannels[userId].Contains))
            .ToList();
        var deliveryRecipients = await ResolveRecipientsAsync(deliveryRecipientIds, cancellationToken);
        var deliveries = new List<NotificationDelivery>();
        foreach (var userId in recipients)
        {
            var recipientTimeZone = recipientTimeZones[userId];
            var taskCommentBody = BuildTaskCommentBody(task, comment, recipientTimeZone);
            var message = RenderTemplate(
                templateDefinition,
                templateStorage,
                taskCommentSubject,
                taskCommentBody,
                NotificationTemplateCatalog.MergeValues(
                    BuildTaskTemplateValues(task, recipientTimeZone, comment.AuthorName),
                    new Dictionary<string, string?>
                    {
                        ["comment.authorName"] = comment.AuthorName,
                        ["comment.text"] = comment.Text,
                    }),
                isSensitive: false,
                purpose: NotificationPurposes.TaskReminder);

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
                    cancellationToken,
                    recipientNotificationChannels[userId]);
            }

            var enabledChannels = channels
                .Where(recipientNotificationChannels[userId].Contains)
                .ToList();
            if (enabledChannels.Count > 0 && deliveryRecipients.TryGetValue(userId, out var recipient))
            {
                deliveries.AddRange(await SendAsync(recipient, message, enabledChannels, eventKey, cancellationToken));
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
        var normalizedChannels = NormalizeChannels(channels);
        SmsNotificationSettingsDto? smsSettings = null;
        SmsRateLimitPolicy? smsRateLimitPolicy = null;
        NotificationMessage? protectedSmsMessage = null;
        SmsMessageValidationResult? smsValidation = null;
        SmsQuietHoursDecision? smsQuietHoursDecision = null;
        var smsMaxUnitCount = SmsMessageValidator.DefaultMaxUnitCount;

        if (normalizedChannels.Contains(NotificationChannels.Sms, StringComparer.OrdinalIgnoreCase))
        {
            var smsPolicy = await GetSmsDeliveryPolicyAsync(cancellationToken);
            if (smsPolicy.EmergencyStop || !smsPolicy.IsEventEnabled(eventKey))
            {
                _logger.LogWarning(
                    "SMS delivery was not created for event hash {EventKeyHash}: {ReasonCode}.",
                    BuildStableTextHash(eventKey ?? "manual"),
                    smsPolicy.EmergencyStop ? "emergency_stop" : "event_disabled");
                normalizedChannels = normalizedChannels
                    .Where(channel => channel != NotificationChannels.Sms)
                    .ToList();
            }
            else
            {
                smsSettings = _smsService is null
                    ? null
                    : await _smsService.GetSettingsAsync(cancellationToken);

                smsRateLimitPolicy = SmsRateLimitPolicy.FromSettings(smsSettings);
                smsQuietHoursDecision = SmsQuietHoursPolicy.Evaluate(
                    smsSettings,
                    _timeProvider.GetUtcNow(),
                    message.IsApprovedSmsCriticalEvent);
                if (!smsQuietHoursDecision.IsAllowed)
                {
                    _logger.LogInformation(
                        "SMS delivery was not created for event hash {EventKeyHash}: {QuietHoursReasonCode}.",
                        BuildStableTextHash(eventKey ?? "manual"),
                        smsQuietHoursDecision.ReasonCode);
                }
                smsMaxUnitCount = smsSettings is { MaxMessageLength: > 0 }
                    ? smsSettings.MaxMessageLength
                    : SmsMessageValidator.DefaultMaxUnitCount;

                if (smsQuietHoursDecision.IsAllowed)
                {
                    var protection = SmsMessageProtector.Protect(message);
                    protectedSmsMessage = message with
                    {
                        Subject = protection.Subject,
                        Body = protection.Body,
                        HtmlBody = null,
                    };
                    smsValidation = SmsMessageValidator.Validate(protectedSmsMessage.Body, smsMaxUnitCount);

                    if (!smsValidation.IsValid)
                    {
                        _logger.LogWarning(
                            "SMS delivery was not created for event hash {EventKeyHash}: {ValidationErrorCode}. Encoding {Encoding}, units {UnitCount}, segments {SegmentCount}, limit {MaxUnitCount}.",
                            BuildStableTextHash(eventKey ?? "manual"),
                            smsValidation.ErrorCode,
                            smsValidation.Calculation.Encoding,
                            smsValidation.Calculation.UnitCount,
                            smsValidation.Calculation.SegmentCount,
                            smsMaxUnitCount);
                    }
                }
            }
        }

        var deliveries = new List<NotificationDelivery>();
        foreach (var channel in normalizedChannels)
        {
            if (channel == NotificationChannels.Sms
                && (smsQuietHoursDecision is { IsAllowed: false }
                    || smsValidation is { IsValid: false }))
            {
                continue;
            }

            var channelMessage = channel == NotificationChannels.Sms
                ? protectedSmsMessage ?? message
                : message;
            var targets = await ResolveSenderTargetsAsync(
                channel,
                channelMessage.Purpose,
                senderId,
                cancellationToken,
                smsSettings);
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
                    Subject = channelMessage.Subject,
                    BodyPreview = channelMessage.IsSensitive ? "Содержимое скрыто: служебное уведомление." : Truncate(channelMessage.Body, 1000),
                    Status = NotificationDeliveryStatus.Pending,
                    CreatedAt = DateTime.UtcNow,
                };

                if (channel == NotificationChannels.Sms)
                {
                    await SmsRateLimitGate.WaitAsync(cancellationToken);
                    try
                    {
                        var policy = smsRateLimitPolicy ?? SmsRateLimitPolicy.FromSettings(smsSettings);
                        var snapshot = await LoadSmsRateLimitSnapshotAsync(
                            delivery.RecipientAddress,
                            delivery.CreatedAt,
                            cancellationToken);
                        var decision = SmsRateLimitEvaluator.Evaluate(policy, snapshot);
                        if (!decision.IsAllowed)
                        {
                            _logger.LogWarning(
                                "SMS delivery was not created for event hash {EventKeyHash}: {RateLimitReasonCode}. Daily {DailyCount}/{DailyLimit}, recipient {RecipientCount}/{RecipientLimit}, burst {BurstCount}/{BurstLimit}.",
                                BuildStableTextHash(delivery.EventKey),
                                decision.ReasonCode,
                                snapshot.DailyCount,
                                policy.DailyLimit,
                                snapshot.RecipientDailyCount,
                                policy.RecipientDailyLimit,
                                snapshot.BurstCount,
                                policy.BurstLimitPerMinute);
                            continue;
                        }

                        _context.NotificationDeliveries.Add(delivery);
                        await _context.SaveChangesAsync(cancellationToken);
                    }
                    finally
                    {
                        SmsRateLimitGate.Release();
                    }
                }
                else
                {
                    _context.NotificationDeliveries.Add(delivery);
                    await _context.SaveChangesAsync(cancellationToken);
                }

                try
                {
                    await SendDeliveryAsync(delivery, target, recipient, channelMessage, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (delivery.Channel == NotificationChannels.Sms)
                    {
                        _logger.LogError(
                            "SMS notification delivery {DeliveryId} failed with {ExceptionType}.",
                            delivery.Id,
                            ex.GetType().Name);
                    }
                    else
                    {
                        _logger.LogError(ex, "Notification delivery {DeliveryId} failed.", delivery.Id);
                    }

                    delivery.Status = NotificationDeliveryStatus.Failed;
                    delivery.Error = delivery.Channel == NotificationChannels.Sms
                        ? "SMS не отправлено: внутренняя ошибка канала."
                        : ex.Message;
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

        if (CaseCourtNotificationDeliveryGuard.IsManaged(original.EventKey))
            return await RetryCourtDeliveryAsync(original, cancellationToken);

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

    public Task<bool> NotifyAsync(
        NotificationEventEnvelope notificationEvent,
        CancellationToken cancellationToken = default)
        => CreateUserNotificationAsync(notificationEvent, cancellationToken);

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
            if (!await CaseCourtNotificationDeliveryGuard.CanSendAsync(_context, delivery.EventKey, delivery.RecipientUserId,
                NotificationChannels.Email, cancellationToken))
            {
                delivery.Status = NotificationDeliveryStatus.Skipped;
                delivery.Error = "Судебное уведомление устарело или больше не разрешено получателю.";
                return;
            }

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

        if (delivery.Channel == NotificationChannels.Sms)
        {
            if (string.IsNullOrWhiteSpace(recipient.PhoneNumber))
            {
                delivery.Status = NotificationDeliveryStatus.Skipped;
                delivery.Error = "У пользователя не указан номер телефона для SMS.";
                return;
            }

            var result = _smsService is null
                ? new SmsSendResult(
                    false,
                    NotificationDeliveryStatus.Skipped,
                    "SMS-уведомления отключены: SMS-сервис не подключен.",
                    ErrorIsSafe: true)
                : await _smsService.SendMessageAsync(
                    recipient.PhoneNumber,
                    message.Body,
                    SmsProviderIdempotencyKey.Create(
                        delivery.EventKey,
                        delivery.RecipientAddress,
                        delivery.NotificationSenderId),
                    cancellationToken);
            delivery.Status = result.Status;
            delivery.Error = ResolveSmsDeliveryError(result);
            delivery.ProviderKey = result.ProviderKey;
            delivery.ProviderMessageId = result.ProviderMessageId;
            delivery.ProviderStatus = result.ProviderStatus;
            delivery.ProviderStatusCheckedAt = result.ProviderStatus is null ? null : DateTime.UtcNow;
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

    private async Task<SmsRateLimitSnapshot> LoadSmsRateLimitSnapshotAsync(
        string recipientAddress,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var dayStartUtc = nowUtc.AddDays(-1);
        var burstStartUtc = nowUtc.AddMinutes(-1);
        var counts = await _context.NotificationDeliveries
            .AsNoTracking()
            .Where(delivery =>
                delivery.Channel == NotificationChannels.Sms
                && delivery.Status != NotificationDeliveryStatus.Skipped
                && delivery.CreatedAt >= dayStartUtc)
            .GroupBy(_ => 1)
            .Select(group => new
            {
                DailyCount = group.Count(),
                RecipientDailyCount = group.Count(delivery => delivery.RecipientAddress == recipientAddress),
                BurstCount = group.Count(delivery => delivery.CreatedAt >= burstStartUtc),
            })
            .SingleOrDefaultAsync(cancellationToken);

        return counts is null
            ? new SmsRateLimitSnapshot(0, 0, 0)
            : new SmsRateLimitSnapshot(
                counts.DailyCount,
                counts.RecipientDailyCount,
                counts.BurstCount);
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

    private Task<bool> CreateUserNotificationAsync(
        int recipientUserId,
        string type,
        string eventKey,
        string title,
        string body,
        string tone,
        TaskJournalItem task,
        object? metadata,
        CancellationToken cancellationToken,
        IReadOnlySet<string>? resolvedNotificationChannels = null,
        bool retryMaxQueue = false,
        bool replayRealtimeOnRetry = false,
        int? expectedApprovalParticipantId = null)
        => CreateUserNotificationAsync(
            new NotificationEventEnvelope(
                recipientUserId,
                type,
                eventKey,
                title,
                body,
                "task",
                task.Id,
                task.Id,
                tone,
                metadata,
                task),
            cancellationToken,
            resolvedNotificationChannels: resolvedNotificationChannels,
            retryMaxQueue: retryMaxQueue,
            replayRealtimeOnRetry: replayRealtimeOnRetry,
            expectedApprovalParticipantId: expectedApprovalParticipantId);

    private Task<bool> CreateUserNotificationAsync(
        int recipientUserId,
        string type,
        string eventKey,
        string title,
        string body,
        string tone,
        string objectType,
        int? objectId,
        int? taskJournalItemId,
        TaskJournalItem? task,
        object? metadata,
        CancellationToken cancellationToken,
        bool objectVisibilityValidated = false,
        IReadOnlySet<string>? resolvedNotificationChannels = null,
        CaseCourtDeliveryClaim? courtClaim = null)
        => CreateUserNotificationAsync(
            new NotificationEventEnvelope(
                recipientUserId,
                type,
                eventKey,
                title,
                body,
                objectType,
                objectId,
                taskJournalItemId,
                tone,
                metadata,
                task),
            cancellationToken,
            objectVisibilityValidated,
            resolvedNotificationChannels,
            courtClaim: courtClaim);

    private async Task<bool> CreateUserNotificationAsync(
        NotificationEventEnvelope notificationEvent,
        CancellationToken cancellationToken,
        bool objectVisibilityValidated = false,
        IReadOnlySet<string>? resolvedNotificationChannels = null,
        bool retryMaxQueue = false,
        bool replayRealtimeOnRetry = false,
        int? expectedApprovalParticipantId = null,
        CaseCourtDeliveryClaim? courtClaim = null)
    {
        if (notificationEvent.RecipientUserId <= 0 || string.IsNullOrWhiteSpace(notificationEvent.EventKey))
        {
            return false;
        }

        var type = notificationEvent.EffectiveType;
        if (string.IsNullOrWhiteSpace(type))
        {
            return false;
        }

        await using var accessLease = replayRealtimeOnRetry && notificationEvent.Task is not null
            ? await TaskDeadlineRequestLease.TryAcquireAsync(_context, notificationEvent.RecipientUserId, notificationEvent.Task.Id, cancellationToken)
            : null;
        await using var courtAccessLease = courtClaim is not null
            ? await CaseCourtNotificationLease.BeginAsync(_context, cancellationToken)
            : null;
        if (courtClaim is not null)
            await PinCourtNotificationClaimAsync(courtClaim, notificationEvent, cancellationToken);
        if (replayRealtimeOnRetry)
        {
            if (notificationEvent.Task is null || accessLease is null)
                throw new InvalidOperationException("Task notification access is temporarily locked.");
            var task = notificationEvent.Task;
            var allowed = await ResolveDurableTaskRecipientsAsync(task.Id, [notificationEvent.RecipientUserId], cancellationToken,
                authorOnly: type == UserNotificationTypes.TaskCompleted);
            var currentToken = await _context.TaskJournalItems.AsNoTracking().Where(item => item.Id == task.Id)
                .Select(item => item.StatusTransitionToken).SingleOrDefaultAsync(cancellationToken);
            if (!allowed.Contains(notificationEvent.RecipientUserId) || currentToken != task.StatusTransitionToken
                || (expectedApprovalParticipantId.HasValue && !await IsApprovalStageCurrentAsync(task.Id, expectedApprovalParticipantId.Value, cancellationToken)))
                return false;
        }

        var inAppEnabled = courtClaim is not null
            ? await IsCourtRecipientCurrentAsync(notificationEvent.RecipientUserId, type, NotificationChannels.InApp, courtClaim, cancellationToken)
            : resolvedNotificationChannels is not null
                ? resolvedNotificationChannels.Contains(NotificationChannels.InApp)
                : await IsUserNotificationChannelEnabledAsync(
                notificationEvent.RecipientUserId,
                type,
                NotificationChannels.InApp,
                cancellationToken);
        if (!inAppEnabled)
        {
            return false;
        }

        var duplicates = _context.UserNotifications.AsNoTracking()
            .Where(UserNotificationAntiSpamPolicy.ExactDuplicatePredicate(notificationEvent));
        if (retryMaxQueue || courtClaim is not null)
        {
            var existing = await duplicates.FirstOrDefaultAsync(cancellationToken);
            if (existing is not null)
            {
                if (courtAccessLease is not null)
                {
                    await courtAccessLease.CommitAsync(cancellationToken);
                    await ReplayCourtNotificationChannelsAsync(existing, courtClaim!, cancellationToken);
                    return false;
                }
                if (accessLease is not null) await accessLease.CommitAsync(cancellationToken);
                if (accessLease is not null && !await IsDurableTaskRecipientCurrentAsync(notificationEvent.Task!, notificationEvent.RecipientUserId,
                    type == UserNotificationTypes.TaskCompleted, cancellationToken, expectedApprovalParticipantId)) return false;
                if (objectVisibilityValidated || await CanRecipientSeeNotificationObjectAsync(notificationEvent, type, cancellationToken))
                {
                    await QueueMaxNotificationIfAvailableAsync(existing, cancellationToken, throwOnFailure: true);
                    if (replayRealtimeOnRetry && !existing.IsRead && !existing.IsArchived && existing.DeletedAt is null)
                    {
                        var toast = resolvedNotificationChannels is not null
                            ? resolvedNotificationChannels.Contains(NotificationChannels.Toast)
                            : await IsUserNotificationChannelEnabledAsync(existing.RecipientUserId, type, NotificationChannels.Toast, cancellationToken);
                        await PublishDurableRealtimeAsync(existing, toast, cancellationToken);
                        var push = resolvedNotificationChannels is not null
                            ? resolvedNotificationChannels.Contains(NotificationChannels.WebPush)
                            : await IsUserNotificationChannelEnabledAsync(existing.RecipientUserId, type, NotificationChannels.WebPush, cancellationToken);
                        if (push && _webPushNotificationService is not null)
                            await _webPushNotificationService.SendUserNotificationAsync(existing, cancellationToken);
                    }
                }
                return false;
            }
        }
        else if (await duplicates.AnyAsync(cancellationToken))
        {
            return false;
        }

        var recipientExists = await _context.Users
            .AsNoTracking()
            .AnyAsync(user => user.Id == notificationEvent.RecipientUserId, cancellationToken);
        if (!recipientExists)
        {
            return false;
        }

        if ((!objectVisibilityValidated || courtClaim is not null)
            && !await CanRecipientSeeNotificationObjectAsync(notificationEvent, type, cancellationToken))
        {
            _logger.LogWarning(
                "User notification {EventKey} for user {RecipientUserId} was skipped because linked object {ObjectType}:{ObjectId} is not visible to recipient.",
                notificationEvent.EventKey,
                notificationEvent.RecipientUserId,
                notificationEvent.ObjectType,
                notificationEvent.ObjectId);
            return false;
        }

        var notification = new UserNotification
        {
            RecipientUserId = notificationEvent.RecipientUserId,
            Type = type,
            EventKey = notificationEvent.EventKey,
            Title = Truncate(notificationEvent.Title.Trim(), 300),
            BodyPreview = Truncate(notificationEvent.BodyPreview.Trim(), 1000),
            Tone = notificationEvent.EffectiveTone,
            ObjectType = notificationEvent.ObjectType,
            ObjectId = notificationEvent.ObjectId,
            TaskJournalItemId = notificationEvent.TaskJournalItemId,
            MetadataJson = notificationEvent.Metadata is null ? "{}" : JsonSerializer.Serialize(notificationEvent.Metadata, JsonOptions),
            CreatedAt = DateTime.UtcNow
        };
        _context.UserNotifications.Add(notification);
        await _context.SaveChangesAsync(cancellationToken);
        if (courtAccessLease is not null)
        {
            await courtAccessLease.CommitAsync(cancellationToken);
            await ReplayCourtNotificationChannelsAsync(notification, courtClaim!, cancellationToken);
            return true;
        }
        if (accessLease is not null) await accessLease.CommitAsync(cancellationToken);
        if (accessLease is not null && !await IsDurableTaskRecipientCurrentAsync(notificationEvent.Task!, notificationEvent.RecipientUserId,
            type == UserNotificationTypes.TaskCompleted, cancellationToken, expectedApprovalParticipantId)) return false;
        await QueueMaxNotificationIfAvailableAsync(notification, cancellationToken, throwOnFailure: retryMaxQueue);
        var toastEnabled = resolvedNotificationChannels is not null
            ? resolvedNotificationChannels.Contains(NotificationChannels.Toast)
            : await IsUserNotificationChannelEnabledAsync(
                notificationEvent.RecipientUserId,
                type,
                NotificationChannels.Toast,
                cancellationToken);
        if (type == UserNotificationTypes.CaseDocument && _context.Database.CurrentTransaction is not null)
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            _context.CaseDocumentNotificationDispatches.Add(new CaseDocumentNotificationDispatch
            {
                UserNotificationId = notification.Id,
                Channel = "realtime",
                ToastEnabled = toastEnabled,
                CreatedAt = now,
                NextAttemptAt = now
            });
            await _context.SaveChangesAsync(cancellationToken);
            return true;
        }
        if (replayRealtimeOnRetry)
            await PublishDurableRealtimeAsync(notification, toastEnabled, cancellationToken);
        else
            await _realtimePublisher.PublishUserNotificationAsync(notification, toastEnabled, cancellationToken);

        var webPushEnabled = resolvedNotificationChannels is not null
            ? resolvedNotificationChannels.Contains(NotificationChannels.WebPush)
            : await IsUserNotificationChannelEnabledAsync(
                notificationEvent.RecipientUserId,
                type,
                NotificationChannels.WebPush,
                cancellationToken);
        if (_webPushNotificationService is not null
            && webPushEnabled)
        {
            await _webPushNotificationService.SendUserNotificationAsync(notification, cancellationToken);
        }

        return true;
    }

    private Task PublishDurableRealtimeAsync(UserNotification notification, bool toastEnabled, CancellationToken ct)
        => _realtimePublisher is IReliableRealtimeNotificationPublisher reliable
            ? reliable.PublishUserNotificationOrThrowAsync(notification, toastEnabled, ct)
            : _realtimePublisher.PublishUserNotificationAsync(notification, toastEnabled, ct);

    public async Task<string> DispatchCaseDocumentAsync(int notificationId, string channel, bool toastEnabled, Func<CancellationToken, Task<bool>> ownsLease, CancellationToken ct)
    {
        if (_context.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Case notification delivery requires a committed context.");
        // CaseDocument is silent in the notification catalog: only update the CRM inbox.
        if (channel != "realtime") return "skipped";
        var notification = await _context.UserNotifications.AsNoTracking()
            .SingleOrDefaultAsync(n => n.Id == notificationId && n.Type == UserNotificationTypes.CaseDocument
                && n.ObjectType == "case" && n.DeletedAt == null && !n.IsArchived, ct);
        if (notification?.ObjectId is not > 0) return "skipped";
        int documentId;
        string? action;
        try
        {
            using var payload = JsonDocument.Parse(notification.MetadataJson);
            var root = payload.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("clientCaseId", out var caseValue) || caseValue.ValueKind != JsonValueKind.Number || !caseValue.TryGetInt32(out var caseId) || caseId != notification.ObjectId
                || !root.TryGetProperty("clientCaseDocumentId", out var idValue) || idValue.ValueKind != JsonValueKind.Number || !idValue.TryGetInt32(out documentId) || documentId <= 0)
                return "skipped";
            action = root.TryGetProperty("action", out var actionValue) && actionValue.ValueKind == JsonValueKind.String ? actionValue.GetString() : null;
        }
        catch (JsonException) { return "skipped"; }
        if (action != "deleted" && !await _context.ClientCaseDocuments.AsNoTracking()
                .AnyAsync(d => d.Id == documentId && d.ClientCaseId == notification.ObjectId, ct)) return "skipped";
        if (!await _context.Users.AsNoTracking().AnyAsync(u => u.Id == notification.RecipientUserId && u.IsActive && u.IsApproved, ct)) return "skipped";
        var envelope = new NotificationEventEnvelope(notification.RecipientUserId, notification.Type, notification.EventKey,
            notification.Title, notification.BodyPreview, notification.ObjectType, notification.ObjectId, null, notification.Tone);
        if (!await CanRecipientSeeNotificationObjectAsync(envelope, notification.Type, ct)
            || !await IsUserNotificationChannelEnabledAsync(notification.RecipientUserId, notification.Type, NotificationChannels.InApp, ct)) return "skipped";
        var toast = toastEnabled && await IsUserNotificationChannelEnabledAsync(notification.RecipientUserId, notification.Type, NotificationChannels.Toast, ct);
        if (!await ownsLease(ct)) return "failed";
        ct.ThrowIfCancellationRequested();
        await _realtimePublisher.PublishUserNotificationAsync(notification, toast, ct);
        return "completed";
    }

    private async Task QueueMaxNotificationIfAvailableAsync(UserNotification notification, CancellationToken cancellationToken, bool throwOnFailure = false)
    {
        if (_maxService is null || _maxOutboxService is null
            || UserNotificationTypes.Find(notification.Type) is not { } typeDefinition
            || NotificationPriorities.IsSilent(typeDefinition.Priority))
            return;

        var baseUri = MaxTaskNotificationPresentation.FrontendUri(_configuration);
        if (baseUri is null) return;
        var taskLink = UserNotificationDomains.Resolve(notification.Type) == UserNotificationDomains.Task
            && notification.ObjectType == "task" && notification.ObjectId is > 0
            && (!notification.TaskJournalItemId.HasValue || notification.TaskJournalItemId == notification.ObjectId)
                ? MaxTaskNotificationPresentation.TaskUri(baseUri, notification.ObjectId.Value) : null;

        try
        {
            var runtime = await (_maxRuntimeSettingsTask ??= _maxService.GetRuntimeSettingsAsync(cancellationToken));
            if (!runtime.Ready || !runtime.NotificationsEnabled
                || !await IsUserNotificationChannelEnabledAsync(
                    notification.RecipientUserId,
                    notification.Type,
                    NotificationChannels.Max,
                    cancellationToken))
                return;

            // An employee without a verified MAX link cannot receive this channel.
            // Treat that as a skip, so recovery does not retry forever or block other channels.
            if (throwOnFailure && !await _context.MaxRecipientLinks.AsNoTracking().AnyAsync(
                    link => link.UserId == notification.RecipientUserId && link.ClientPortalAccountId == null
                        && link.Status == MaxRecipientLinkStatuses.Verified && link.MaxUserId != null, cancellationToken))
                return;

            await _maxOutboxService.QueueAsync(new MaxQueueRequest(
                notification.RecipientUserId,
                MaxMessengerScenarios.Notifications,
                notification.EventKey,
                string.Empty,
                taskLink is null ? "Открыть CRM" : "Открыть задачу",
                (taskLink ?? baseUri).AbsoluteUri,
                notification.Type), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Отказ дополнительного канала не должен скрывать уже сохранённое уведомление CRM.
            _logger.LogWarning("MAX notification was not queued for user {RecipientUserId}, type {Type}: {ErrorType}.",
                notification.RecipientUserId, notification.Type, exception.GetType().Name);
            if (_maxRuntimeSettingsTask?.IsFaulted == true) _maxRuntimeSettingsTask = null;
            if (throwOnFailure) throw;
        }
    }

    private async Task<bool> CanRecipientSeeNotificationObjectAsync(
        NotificationEventEnvelope notificationEvent,
        string type,
        CancellationToken cancellationToken)
    {
        if (!notificationEvent.ObjectId.HasValue
            || string.IsNullOrWhiteSpace(notificationEvent.ObjectType)
            || IsNotificationObjectVisibilityBypassed(type))
        {
            return true;
        }

        var objectId = notificationEvent.ObjectId.Value;
        if (objectId <= 0)
        {
            return false;
        }

        var objectType = notificationEvent.ObjectType.Trim().ToLowerInvariant();
        if (objectType == "task" && notificationEvent.Task is not null)
        {
            return notificationEvent.Task.SalesRequestId is not int requestId
                || await _context.Users.WithSalesRequestAccess(requestId, _context)
                    .AnyAsync(user => user.Id == notificationEvent.RecipientUserId, cancellationToken);
        }

        return objectType switch
        {
            "task" => await CanRecipientSeeDataScopedObjectAsync(
                notificationEvent.RecipientUserId,
                RolePermissionCatalog.Tasks,
                async (access, ct) => await _context.TaskJournalItems
                    .AsNoTracking()
                    .Where(item => item.Id == objectId)
                    .ApplyTaskVisibility(access, _context)
                    .AnyAsync(ct),
                cancellationToken),
            "payment" => await CanRecipientSeeDataScopedObjectAsync(
                notificationEvent.RecipientUserId,
                RolePermissionCatalog.Payments,
                async (access, ct) => await _context.Payments
                    .AsNoTracking()
                    .Where(item => item.Id == objectId)
                    .ApplyPaymentVisibility(access)
                    .AnyAsync(ct),
                cancellationToken),
            "invoice" or "account" => await CanRecipientSeeDataScopedObjectAsync(
                notificationEvent.RecipientUserId,
                RolePermissionCatalog.Accounts,
                async (access, ct) => await _context.Payments
                    .AsNoTracking()
                    .Where(item => item.Id == objectId)
                    .ApplyPaymentVisibility(access)
                    .AnyAsync(ct),
                cancellationToken),
            "act" => await CanRecipientSeeDataScopedObjectAsync(
                notificationEvent.RecipientUserId,
                RolePermissionCatalog.Acts,
                async (access, ct) => await _context.Acts
                    .AsNoTracking()
                    .Where(item => item.Id == objectId)
                    .ApplyActVisibility(access)
                    .AnyAsync(ct),
                cancellationToken),
            "contract" => await CanRecipientSeeDataScopedObjectAsync(
                notificationEvent.RecipientUserId,
                RolePermissionCatalog.Contracts,
                async (access, ct) => await _context.Contracts
                    .AsNoTracking()
                    .Where(item => item.Id == objectId)
                    .ApplyContractVisibility(access)
                    .AnyAsync(ct),
                cancellationToken),
            "case" or "clientcase" or "client-case" => await CanRecipientSeeDataScopedObjectAsync(
                notificationEvent.RecipientUserId,
                RolePermissionCatalog.Cases,
                async (access, ct) => await _context.ClientCases
                    .AsNoTracking()
                    .Where(item => item.Id == objectId)
                    .ApplyClientCaseVisibility(access)
                    .AnyAsync(ct),
                cancellationToken),
            "note" or "stickynote" or "sticky-note" => await CanRecipientSeeStickyNoteAsync(
                notificationEvent.RecipientUserId,
                objectId,
                cancellationToken),
            _ => true
        };
    }

    private static bool IsNotificationObjectVisibilityBypassed(string type)
        => type is UserNotificationTypes.CaseAccessRequested
            or UserNotificationTypes.CaseAccessResolved;

    private async Task<bool> CanRecipientSeeDataScopedObjectAsync(
        int recipientUserId,
        string section,
        Func<DataVisibilityAccess, CancellationToken, Task<bool>> canSeeObjectAsync,
        CancellationToken cancellationToken)
    {
        var principal = CreateNotificationRecipientPrincipal(recipientUserId);
        var access = await _context.ResolveDataVisibilityAsync(principal, section, cancellationToken);
        return access is not null && await canSeeObjectAsync(access, cancellationToken);
    }

    private async Task<bool> CanRecipientSeeStickyNoteAsync(
        int recipientUserId,
        int noteId,
        CancellationToken cancellationToken)
    {
        var principal = CreateNotificationRecipientPrincipal(recipientUserId);
        var canViewNotes = await _context.HasUserSectionPermissionAsync(
            principal,
            RolePermissionCatalog.Notes,
            permission => permission.CanView,
            cancellationToken);
        if (!canViewNotes)
        {
            return false;
        }

        return await _context.StickyNotes
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(note => note.Id == noteId)
            .AnyAsync(note =>
                note.Board != null
                && !note.Board.IsArchived
                && (note.Board.Visibility == StickyNoteBoardVisibility.Shared
                    || note.Board.OwnerUserId == recipientUserId
                    || note.Board.Members.Any(member => member.UserId == recipientUserId)
                    || note.AssignedUserId == recipientUserId
                    || note.AuthorUserId == recipientUserId),
                cancellationToken);
    }

    private async Task<bool> IsUserNotificationTypeEnabledAsync(
        int recipientUserId,
        string type,
        CancellationToken cancellationToken)
    {
        var preferences = await GetUserNotificationPreferencesAsync(recipientUserId, cancellationToken);
        return UserNotificationPreferenceSettings.IsTypeEnabled(preferences, type);
    }

    private async Task<bool> IsUserNotificationChannelEnabledAsync(
        int recipientUserId,
        string type,
        string channel,
        CancellationToken cancellationToken)
    {
        if (!IsChannelAvailableForNotificationType(type, channel))
        {
            return false;
        }

        if (channel == NotificationChannels.Sms
            && !await IsSmsEventEnabledAsync(type, cancellationToken))
        {
            return false;
        }

        var preferences = await GetUserNotificationPreferencesAsync(recipientUserId, cancellationToken);
        return UserNotificationPreferenceSettings.IsChannelEnabled(preferences, type, channel)
               && !await IsUserNotificationChannelMutedAsync(recipientUserId, type, channel, cancellationToken);
    }

    private async Task<IReadOnlyList<string>> ResolveUserNotificationChannelsAsync(
        int recipientUserId,
        string type,
        IEnumerable<string> channels,
        CancellationToken cancellationToken,
        TaskJournalItem? task = null)
    {
        if (task?.SalesRequestId is int requestId
            && !await _context.Users.WithSalesRequestAccess(requestId, _context).AnyAsync(user => user.Id == recipientUserId, cancellationToken))
            return [];

        var normalizedChannels = NormalizeChannels(channels);
        if (normalizedChannels.Count == 0)
        {
            return [];
        }

        var preferences = await GetUserNotificationPreferencesAsync(recipientUserId, cancellationToken);
        var quietHours = await GetUserNotificationDoNotDisturbAsync(recipientUserId, cancellationToken);
        var timeZone = quietHours.Enabled
            ? await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_context, recipientUserId, cancellationToken)
            : TimeZoneInfo.Utc;
        var typeDefinition = UserNotificationTypes.Find(type);
        var nowUtc = DateTime.UtcNow;
        var smsPolicy = normalizedChannels.Contains(NotificationChannels.Sms, StringComparer.OrdinalIgnoreCase)
            ? await GetSmsDeliveryPolicyAsync(cancellationToken)
            : SmsDeliveryPolicy.AllowAll;

        return normalizedChannels
            .Where(channel => IsChannelAvailableForNotificationType(type, channel))
            .Where(channel => channel != NotificationChannels.Sms
                              || (!smsPolicy.EmergencyStop && smsPolicy.IsEventEnabled(type)))
            .Where(channel => UserNotificationPreferenceSettings.IsChannelEnabled(preferences, type, channel))
            .Where(channel => !UserNotificationDoNotDisturbSettings.IsChannelMuted(
                quietHours,
                typeDefinition,
                channel,
                nowUtc,
                timeZone))
            .ToList();
    }

    private async Task<IReadOnlyDictionary<int, IReadOnlySet<string>>> ResolveUserNotificationChannelsAsync(
        IEnumerable<int> recipientUserIds,
        string type,
        IEnumerable<string> channels,
        CancellationToken cancellationToken,
        TaskJournalItem? task = null)
    {
        var userIds = recipientUserIds
            .Where(userId => userId > 0)
            .Distinct()
            .ToList();
        if (userIds.Count == 0)
        {
            return new Dictionary<int, IReadOnlySet<string>>();
        }

        var normalizedChannels = channels
            .Select(channel => NotificationChannels.UserPreferences.FirstOrDefault(candidate =>
                string.Equals(candidate, channel?.Trim(), StringComparison.OrdinalIgnoreCase)))
            .Where(channel => channel is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalizedChannels.Count == 0)
        {
            return userIds.ToDictionary(
                userId => userId,
                _ => (IReadOnlySet<string>)new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        HashSet<int>? requestRecipients = null;
        if (task?.SalesRequestId is int requestId)
        {
            requestRecipients = (await _context.Users.Where(user => userIds.Contains(user.Id))
                .WithSalesRequestAccess(requestId, _context).Select(user => user.Id).ToListAsync(cancellationToken)).ToHashSet();
            if (requestRecipients.Count == 0)
                return userIds.ToDictionary(userId => userId, _ => (IReadOnlySet<string>)new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        var settingRows = await _context.UserSettings
            .AsNoTracking()
            .Where(item => userIds.Contains(item.UserId)
                           && (item.Key == UserNotificationPreferenceSettings.SettingKey
                               || item.Key == UserNotificationDoNotDisturbSettings.SettingKey))
            .Select(item => new { item.UserId, item.Key, item.Value })
            .ToListAsync(cancellationToken);
        var settingsByUser = settingRows
            .GroupBy(item => item.UserId)
            .ToDictionary(
                group => group.Key,
                group => group.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal));

        var preferencesByUser = new Dictionary<int, UserNotificationPreferenceSnapshot>();
        var usersWithoutPersonalPreferences = new List<int>();
        foreach (var userId in userIds)
        {
            if (settingsByUser.TryGetValue(userId, out var userSettings)
                && userSettings.TryGetValue(UserNotificationPreferenceSettings.SettingKey, out var rawPreferences))
            {
                preferencesByUser[userId] = UserNotificationPreferenceSettings.Parse(rawPreferences);
            }
            else
            {
                usersWithoutPersonalPreferences.Add(userId);
            }
        }

        if (usersWithoutPersonalPreferences.Count > 0)
        {
            var rolesByUser = await _context.Users
                .AsNoTracking()
                .Where(user => usersWithoutPersonalPreferences.Contains(user.Id))
                .Select(user => new
                {
                    user.Id,
                    user.RoleId,
                    RoleName = user.Role != null ? user.Role.Name : string.Empty,
                })
                .ToDictionaryAsync(user => user.Id, cancellationToken);
            var roleSetting = rolesByUser.Values.Any(role => role.RoleId > 0)
                ? await _context.SystemSettings
                    .AsNoTracking()
                    .Where(item => item.Key == RoleNotificationPreferenceSettings.SettingKey)
                    .Select(item => item.Value)
                    .FirstOrDefaultAsync(cancellationToken)
                : null;

            foreach (var userId in usersWithoutPersonalPreferences)
            {
                if (!rolesByUser.TryGetValue(userId, out var role) || role.RoleId <= 0)
                {
                    preferencesByUser[userId] = UserNotificationPreferenceSettings.Empty;
                    continue;
                }

                preferencesByUser[userId] = RoleNotificationPreferenceSettings.ParseForRole(roleSetting, role.RoleId)
                    ?? RoleNotificationPreferenceDefaults.Resolve(role.RoleName).Preferences;
            }
        }

        var doNotDisturbByUser = userIds.ToDictionary(
            userId => userId,
            userId => settingsByUser.TryGetValue(userId, out var userSettings)
                      && userSettings.TryGetValue(UserNotificationDoNotDisturbSettings.SettingKey, out var rawDoNotDisturb)
                ? UserNotificationDoNotDisturbSettings.Parse(rawDoNotDisturb)
                : UserNotificationDoNotDisturbSettings.Disabled);
        var usersWithDoNotDisturb = doNotDisturbByUser
            .Where(item => item.Value.Enabled)
            .Select(item => item.Key)
            .ToList();
        var timeZonesByUser = await CalendarTimeZoneHelper.ResolveUserTimeZonesAsync(
            _context,
            usersWithDoNotDisturb,
            cancellationToken);
        var typeDefinition = UserNotificationTypes.Find(type);
        var nowUtc = DateTime.UtcNow;
        var smsPolicy = normalizedChannels.Contains(NotificationChannels.Sms, StringComparer.OrdinalIgnoreCase)
            ? await GetSmsDeliveryPolicyAsync(cancellationToken)
            : SmsDeliveryPolicy.AllowAll;

        return userIds.ToDictionary(
            userId => userId,
            userId => (IReadOnlySet<string>)normalizedChannels
                .Where(_ => requestRecipients is null || requestRecipients.Contains(userId))
                .Where(channel => IsChannelAvailableForNotificationType(type, channel))
                .Where(channel => channel != NotificationChannels.Sms
                                  || (!smsPolicy.EmergencyStop && smsPolicy.IsEventEnabled(type)))
                .Where(channel => UserNotificationPreferenceSettings.IsChannelEnabled(preferencesByUser[userId], type, channel))
                .Where(channel => !UserNotificationDoNotDisturbSettings.IsChannelMuted(
                    doNotDisturbByUser[userId],
                    typeDefinition,
                    channel,
                    nowUtc,
                    timeZonesByUser.GetValueOrDefault(userId, TimeZoneInfo.Utc)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    private async Task<bool> IsSmsEventEnabledAsync(
        string eventType,
        CancellationToken cancellationToken)
    {
        var policy = await GetSmsDeliveryPolicyAsync(cancellationToken);
        return !policy.EmergencyStop && policy.IsEventEnabled(eventType);
    }

    private async Task<SmsDeliveryPolicy> GetSmsDeliveryPolicyAsync(CancellationToken cancellationToken)
    {
        if (_smsDeliveryPolicyCache is not null)
        {
            return _smsDeliveryPolicyCache;
        }

        _smsDeliveryPolicyCache = _smsService is null
            ? SmsDeliveryPolicy.AllowAll
            : await _smsService.GetDeliveryPolicyAsync(cancellationToken);
        return _smsDeliveryPolicyCache;
    }

    private static bool IsChannelAvailableForNotificationType(string type, string channel)
    {
        var typeDefinition = UserNotificationTypes.Find(type);
        return typeDefinition is null
               || typeDefinition.PreferenceChannels.Contains(channel, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<bool> IsUserNotificationChannelMutedAsync(
        int recipientUserId,
        string type,
        string channel,
        CancellationToken cancellationToken)
    {
        var quietHours = await GetUserNotificationDoNotDisturbAsync(recipientUserId, cancellationToken);
        if (!quietHours.Enabled)
        {
            return false;
        }

        var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_context, recipientUserId, cancellationToken);
        return UserNotificationDoNotDisturbSettings.IsChannelMuted(
            quietHours,
            UserNotificationTypes.Find(type),
            channel,
            DateTime.UtcNow,
            timeZone);
    }

    private async Task<UserNotificationDoNotDisturbSnapshot> GetUserNotificationDoNotDisturbAsync(
        int recipientUserId,
        CancellationToken cancellationToken)
    {
        var userSetting = await _context.UserSettings
            .AsNoTracking()
            .Where(item =>
                item.UserId == recipientUserId
                && item.Key == UserNotificationDoNotDisturbSettings.SettingKey)
            .Select(item => item.Value)
            .FirstOrDefaultAsync(cancellationToken);

        return UserNotificationDoNotDisturbSettings.Parse(userSetting);
    }

    private async Task<UserNotificationPreferenceSnapshot> GetUserNotificationPreferencesAsync(
        int recipientUserId,
        CancellationToken cancellationToken)
    {
        var userSetting = await _context.UserSettings
            .AsNoTracking()
            .Where(item =>
                item.UserId == recipientUserId
                && item.Key == UserNotificationPreferenceSettings.SettingKey)
            .Select(item => item.Value)
            .FirstOrDefaultAsync(cancellationToken);
        if (userSetting is not null)
        {
            return UserNotificationPreferenceSettings.Parse(userSetting);
        }

        var role = await _context.Users
            .AsNoTracking()
            .Where(user => user.Id == recipientUserId)
            .Select(user => new
            {
                user.RoleId,
                RoleName = user.Role != null ? user.Role.Name : string.Empty
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (role is null || role.RoleId <= 0)
        {
            return UserNotificationPreferenceSettings.Empty;
        }

        var roleSetting = await _context.SystemSettings
            .AsNoTracking()
            .Where(item => item.Key == RoleNotificationPreferenceSettings.SettingKey)
            .Select(item => item.Value)
            .FirstOrDefaultAsync(cancellationToken);

        return RoleNotificationPreferenceSettings.ParseForRole(roleSetting, role.RoleId)
               ?? RoleNotificationPreferenceDefaults.Resolve(role.RoleName).Preferences;
    }

    private async Task<IReadOnlyList<NotificationSenderTarget>> ResolveSenderTargetsAsync(
        string channel,
        string purpose,
        int? senderId,
        CancellationToken cancellationToken,
        SmsNotificationSettingsDto? smsSettings = null)
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

        var legacyTarget = await BuildLegacySenderTargetAsync(channel, cancellationToken, smsSettings);
        return legacyTarget is null ? [] : [legacyTarget];
    }

    private async Task<NotificationSenderTarget?> BuildLegacySenderTargetAsync(
        string channel,
        CancellationToken cancellationToken,
        SmsNotificationSettingsDto? smsSettings = null)
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

        if (channel == NotificationChannels.Sms)
        {
            var sms = smsSettings ?? (_smsService is null
                ? new SmsNotificationSettingsDto
                {
                    Enabled = false,
                    Ready = false,
                    ProviderConfigured = false,
                    AdapterConfigured = false,
                    ApiTokenSet = false,
                    StatusText = "SMS-уведомления отключены: SMS-сервис не подключен."
                }
                : await _smsService.GetSettingsAsync(cancellationToken));
            var reason = string.IsNullOrWhiteSpace(sms.StatusText)
                ? sms.Enabled
                    ? "SMS-провайдер не настроен."
                    : "SMS-уведомления отключены."
                : sms.StatusText;
            return new NotificationSenderTarget(
                null,
                channel,
                string.IsNullOrWhiteSpace(sms.SenderName) ? "SMS по умолчанию" : sms.SenderName,
                sms.Ready,
                null,
                null,
                null,
                reason);
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

    private async Task<IReadOnlyDictionary<int, NotificationRecipient>> ResolveRecipientsAsync(
        IEnumerable<int> userIds,
        CancellationToken cancellationToken)
    {
        var normalizedUserIds = userIds
            .Where(userId => userId > 0)
            .Distinct()
            .ToArray();
        if (normalizedUserIds.Length == 0)
        {
            return new Dictionary<int, NotificationRecipient>();
        }

        var users = await _context.Users
            .AsNoTracking()
            .Include(item => item.ExternalIdentities)
            .Where(item => normalizedUserIds.Contains(item.Id))
            .ToListAsync(cancellationToken);
        return users.ToDictionary(item => item.Id, BuildRecipient);
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
            telegramChatId,
            NormalizePhoneNumber(user.PhoneNumber));
    }

    private static string DisplayName(User user)
        => string.IsNullOrWhiteSpace(user.FullName) ? user.Email : user.FullName.Trim();

    private static IReadOnlyList<string> NormalizeChannels(IEnumerable<string>? channels)
    {
        return (channels ?? [NotificationChannels.Email])
            .Select(NormalizeChannel)
            .Where(channel => channel is NotificationChannels.Email or NotificationChannels.Telegram or NotificationChannels.Sms)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToList();
    }

    private static string NormalizeChannel(string? channel)
        => (channel ?? string.Empty).Trim().ToLowerInvariant();

    private static IReadOnlyList<string> NormalizePurposes(string channel, IEnumerable<string>? purposes)
    {
        var normalizedChannel = NormalizeChannel(channel);
        var normalized = (purposes ?? [])
            .Select(NormalizePurpose)
            .Where(purpose => purpose is not null)
            .Cast<string>()
            .Where(purpose => PurposeDefinitions.Any(definition =>
                string.Equals(definition.Key, purpose, StringComparison.OrdinalIgnoreCase)
                && definition.Channels.Contains(normalizedChannel, StringComparer.OrdinalIgnoreCase)))
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
            : channel == NotificationChannels.Sms
                ? recipient.PhoneNumber ?? string.Empty
            : recipient.Email ?? string.Empty;

    private static string? NormalizePhoneNumber(string? value)
        => SmsPhoneNumberSelector.Select(
            [new SmsPhoneNumberCandidate(
                value,
                SmsPhoneNumberKinds.Mobile,
                IsPrimary: true,
                IsVerified: true)]).E164;

    private static NotificationSenderDto MapSender(NotificationSender sender, int subscriptionsCount = 0)
    {
        var purposes = NormalizePurposes(sender.Channel, DeserializeList(sender.PurposesJson));
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

    private async Task<NotificationTemplateStorage> LoadTemplateStorageAsync(CancellationToken cancellationToken)
    {
        var raw = await _context.SystemSettings
            .AsNoTracking()
            .Where(setting => setting.Key == NotificationTemplatesKey)
            .Select(setting => setting.Value)
            .FirstOrDefaultAsync(cancellationToken);

        return DeserializeConfig<NotificationTemplateStorage>(raw);
    }

    private async Task SaveTemplateStorageAsync(NotificationTemplateStorage storage, CancellationToken cancellationToken)
    {
        var catalog = NotificationTemplateCatalog.All.Select(item => item.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        storage.Templates = storage.Templates
            .Where(item => catalog.Contains(item.Key))
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToList();

        await UpsertSettingAsync(
            NotificationTemplatesKey,
            JsonSerializer.Serialize(storage, JsonOptions),
            DateTime.UtcNow,
            cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }

    private static NotificationTemplateDto MapTemplate(
        NotificationTemplateDefinition definition,
        NotificationTemplateStorage storage)
    {
        var item = storage.Templates.FirstOrDefault(template => string.Equals(template.Key, definition.Key, StringComparison.OrdinalIgnoreCase));
        return new NotificationTemplateDto
        {
            Key = definition.Key,
            Label = definition.Label,
            Description = definition.Description,
            IsEnabled = item?.IsEnabled ?? true,
            Subject = string.IsNullOrWhiteSpace(item?.Subject) ? definition.DefaultSubject : item.Subject,
            Body = string.IsNullOrWhiteSpace(item?.Body) ? definition.DefaultBody : item.Body,
            DefaultSubject = definition.DefaultSubject,
            DefaultBody = definition.DefaultBody,
            Variables = definition.Variables.Select(variable => new NotificationTemplateVariableDto
            {
                Key = variable.Key,
                Label = variable.Label
            }).ToList()
        };
    }

    private static string NormalizeTemplateText(string? value, string fallback)
    {
        var normalized = NotificationTemplateCatalog.NormalizeLineBreaks(value ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
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

    private static Dictionary<string, string?> BuildUserTemplateValues(User user)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["user.name"] = DisplayName(user),
            ["user.email"] = user.Email ?? string.Empty,
        };

    private Task<TimeZoneInfo> ResolveTaskRecipientTimeZoneAsync(int userId, CancellationToken cancellationToken)
        => CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_context, userId, cancellationToken);

    private static string FormatTaskDueAt(DateTime dueAt, TimeZoneInfo timeZone)
        => CalendarTimeZoneHelper.FormatUserDateTime(dueAt, timeZone, culture: RuCulture);

    private static Dictionary<string, string?> BuildTaskTemplateValues(
        TaskJournalItem task,
        TimeZoneInfo timeZone,
        string? actorName = null)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["task.title"] = task.Title,
            ["task.objectName"] = task.ObjectName,
            ["task.dueAt"] = FormatTaskDueAt(task.DueAt, timeZone),
            ["task.status"] = task.Status,
            ["task.priority"] = task.Priority,
            ["task.assigneeName"] = string.IsNullOrWhiteSpace(task.AssigneeName) ? "не указан" : task.AssigneeName.Trim(),
            ["task.authorName"] = string.IsNullOrWhiteSpace(task.AuthorName) ? "не указан" : task.AuthorName.Trim(),
            ["task.result"] = string.IsNullOrWhiteSpace(task.Result) ? "не заполнен" : task.Result.Trim(),
            ["actor.name"] = string.IsNullOrWhiteSpace(actorName) ? "Система" : actorName.Trim(),
        };

    private static Dictionary<string, string?> BuildTaskAssignmentTemplateValues(
        TaskJournalItem task,
        TimeZoneInfo timeZone,
        string? actorName,
        string assignedSubject,
        string? assignmentDetails = null)
        => NotificationTemplateCatalog.MergeValues(
            BuildTaskTemplateValues(task, timeZone, actorName),
            new Dictionary<string, string?>
            {
                ["task.assignedSubject"] = assignedSubject,
                ["task.assignmentDetails"] = assignmentDetails ?? string.Empty,
            });

    private static string BuildConflictSummary(IReadOnlyList<TaskJournalItem> conflicts, TimeZoneInfo timeZone)
        => string.Join(
            Environment.NewLine,
            conflicts.Select(item =>
                $"- {item.Title} · {FormatTaskDueAt(item.DueAt, timeZone)}"));

    private static string BuildTaskReminderSubject(TaskJournalItem task, string reminderKey, DateTime nowUtc)
        => reminderKey switch
        {
            "dailyOverdue" => $"Просрочена задача: {task.Title}",
            "atDue" => $"Наступил срок задачи: {task.Title}",
            _ => $"Напоминание о задаче: {task.Title}"
        };

    private static string BuildTaskReminderBody(
        TaskJournalItem task,
        string reminderKey,
        DateTime nowUtc,
        TimeZoneInfo timeZone)
    {
        var dueText = FormatTaskDueAt(task.DueAt, timeZone);
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

    private static string BuildTaskAssignedBody(TaskJournalItem task, string actorName, TimeZoneInfo timeZone)
    {
        var dueText = FormatTaskDueAt(task.DueAt, timeZone);
        var lines = new List<string>
        {
            $"Задача: {task.Title}",
            $"Постановщик: {actorName}",
            $"Клиент/дело: {task.ObjectName}",
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

    private static string BuildTaskReassignedToNewAssigneeBody(
        TaskJournalItem task,
        IReadOnlyList<string> changeLabels,
        string actorName,
        string? previousAssigneeName,
        string? comment,
        TimeZoneInfo timeZone)
    {
        var dueText = FormatTaskDueAt(task.DueAt, timeZone);
        var lines = new List<string>
        {
            $"{actorName} назначил(а) задачу вам.",
            string.Empty,
            $"Клиент/дело: {task.ObjectName}",
            $"Задача: {task.Title}",
            $"Срок: {dueText}",
            $"Предыдущий исполнитель: {FormatNotificationName(previousAssigneeName)}",
            $"Новый исполнитель: {FormatNotificationName(task.AssigneeName)}",
        };

        AppendTaskChangeDetails(lines, FormatTaskChangeLabels(changeLabels, timeZone), comment);
        return string.Join('\n', lines);
    }

    private static string BuildTaskReassignedFromPreviousAssigneeBody(
        TaskJournalItem task,
        IReadOnlyList<string> changeLabels,
        string actorName,
        string? previousAssigneeName,
        string? comment,
        TimeZoneInfo timeZone)
    {
        var dueText = FormatTaskDueAt(task.DueAt, timeZone);
        var lines = new List<string>
        {
            $"{actorName} изменил(а) исполнителя задачи.",
            string.Empty,
            $"Клиент/дело: {task.ObjectName}",
            $"Задача: {task.Title}",
            $"Срок: {dueText}",
            $"Предыдущий исполнитель: {FormatNotificationName(previousAssigneeName)}",
            $"Новый исполнитель: {FormatNotificationName(task.AssigneeName)}",
        };

        AppendTaskChangeDetails(lines, changeLabels, comment);
        return string.Join('\n', lines);
    }

    private static string BuildTaskCompletedBody(TaskJournalItem task, TimeZoneInfo timeZone)
    {
        var dueText = FormatTaskDueAt(task.DueAt, timeZone);
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

    private static string BuildTaskCancelledBody(
        TaskJournalItem task,
        string actorName,
        string action,
        TimeZoneInfo timeZone)
    {
        var dueText = FormatTaskDueAt(task.DueAt, timeZone);
        var lines = new List<string>
        {
            $"{actorName} отменил(а) календарную задачу.",
            string.Empty,
            $"Клиент/дело: {task.ObjectName}",
            $"Задача: {task.Title}",
            $"Срок: {dueText}",
            $"Действие: {action}",
        };

        if (!string.IsNullOrWhiteSpace(task.AssigneeName))
        {
            lines.Add($"Исполнитель: {task.AssigneeName.Trim()}");
        }

        return string.Join('\n', lines);
    }

    private static string BuildCalendarAccessChangedBody(
        string actorName,
        string addedText,
        string removedText)
        => string.Join('\n', new[]
        {
            $"{actorName} изменил(а) список календарей сотрудников, доступных вашей роли.",
            string.Empty,
            $"Добавлены: {addedText}",
            $"Убраны: {removedText}",
            string.Empty,
            "Обновите раздел календаря, чтобы увидеть актуальный список сотрудников."
        });

    private static string BuildExternalCalendarSyncWarningBody(
        string providerName,
        string operation,
        string summary,
        string details)
    {
        var lines = new List<string>
        {
            $"Внешний календарь {providerName} сообщил о проблеме.",
            string.Empty,
            $"Операция: {operation}",
            $"Что произошло: {summary}",
        };

        if (!string.IsNullOrWhiteSpace(details))
        {
            lines.Add($"Детали: {details.Trim()}");
        }

        lines.Add(string.Empty);
        lines.Add("Проверьте подключение календаря и журнал синхронизации.");
        return string.Join('\n', lines);
    }

    private static string BuildStickyNoteMentionBody(
        string actorName,
        string boardTitle,
        string preview)
        => string.Join('\n', new[]
        {
            $"{actorName} упомянул(а) вас на доске заметок.",
            string.Empty,
            $"Доска: {boardTitle}",
            $"Заметка: {preview}",
            string.Empty,
            "Откройте раздел «Заметки», чтобы посмотреть сообщение."
        });

    private static string BuildStickyNoteAssignedBody(
        string actorName,
        string boardTitle,
        string preview,
        string reminderText)
        => string.Join('\n', new[]
        {
            $"{actorName} назначил(а) вам заметку.",
            string.Empty,
            $"Доска: {boardTitle}",
            $"Срок: {reminderText}",
            $"Заметка: {preview}",
            string.Empty,
            "Откройте раздел «Заметки», чтобы посмотреть или обновить напоминание."
        });

    private static string BuildStickyNoteReminderBody(
        string boardTitle,
        string preview,
        string reminderText)
        => string.Join('\n', new[]
        {
            "Наступил срок заметки.",
            string.Empty,
            $"Доска: {boardTitle}",
            $"Срок: {reminderText}",
            $"Заметка: {preview}",
            string.Empty,
            "Откройте раздел «Заметки», чтобы вернуться к напоминанию."
        });

    private static string BuildStickyNoteImportantChangeBody(
        string actorName,
        string action,
        string boardTitle,
        string preview)
        => string.Join('\n', new[]
        {
            $"{actorName} {action} на общей доске заметок.",
            string.Empty,
            $"Доска: {boardTitle}",
            $"Заметка: {preview}",
            string.Empty,
            "Откройте раздел «Заметки», чтобы посмотреть важное изменение."
        });

    private static string BuildCaseInactivityControlBody(
        TaskJournalItem task,
        int daysWithoutActiveTask,
        TimeZoneInfo timeZone)
    {
        var dueText = FormatTaskDueAt(task.DueAt, timeZone);
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

    private static string BuildTaskScheduleConflictBody(
        TaskJournalItem task,
        IReadOnlyList<TaskJournalItem> conflicts,
        string actorName,
        TimeZoneInfo timeZone)
    {
        var dueText = FormatTaskDueAt(task.DueAt, timeZone);
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
            var conflictDue = FormatTaskDueAt(conflict.DueAt, timeZone);
            lines.Add($"- {conflictDue}: {conflict.Title}");
        }

        return string.Join('\n', lines);
    }

    private static string BuildTaskApprovalRequestedBody(
        TaskJournalItem task,
        string actorName,
        TaskJournalParticipant? approver,
        TimeZoneInfo timeZone)
    {
        var dueText = FormatTaskDueAt(task.DueAt, timeZone);
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
        string? resolution,
        TimeZoneInfo timeZone)
    {
        var dueText = FormatTaskDueAt(task.DueAt, timeZone);
        var isRevision = string.Equals(decision, "revisionRequested", StringComparison.OrdinalIgnoreCase);
        var isCompletedApproval = !isRevision && string.Equals(task.Status, "done", StringComparison.OrdinalIgnoreCase);
        var actionText = isRevision
            ? "вернул(а) задачу на доработку"
            : isCompletedApproval
                ? "согласовал(а) и завершил(а) задачу"
                : "согласовал(а) задачу";
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
            lines.Add(isRevision ? $"Рецензия: {resolution.Trim()}" : $"Резолюция: {resolution.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(task.Result))
        {
            lines.Add(string.Empty);
            lines.Add($"Результат исполнителя: {task.Result.Trim()}");
        }

        if (isRevision)
        {
            lines.Add(string.Empty);
            lines.Add("После исправления внесите правки и повторно направьте задачу на согласование.");
            lines.Add("Согласующие, которые уже согласовали задачу, повторно в маршруте не участвуют.");
        }

        return string.Join('\n', lines);
    }

    private static string BuildTaskDeadlineChangeRequestBody(
        TaskJournalItem task,
        TaskJournalDeadlineChangeRequest request,
        TimeZoneInfo timeZone)
    {
        var currentDueAt = FormatTaskDueAt(request.CurrentDueAt, timeZone);
        var requestedDueAt = FormatTaskDueAt(request.RequestedDueAt, timeZone);
        var lines = new List<string>
        {
            $"{request.RequestedByName} запросил(а) перенос срока по задаче.",
            string.Empty,
            $"Клиент/дело: {task.ObjectName}",
            $"Задача: {task.Title}",
            $"Текущий срок: {currentDueAt}",
            $"Запрошенный срок: {requestedDueAt}",
            $"Постановщик: {FormatNotificationName(request.ReviewerName)}",
            string.Empty,
            $"Причина: {request.Reason.Trim()}",
            string.Empty,
            "Откройте раздел \"Согласования\", чтобы согласовать перенос или отказать."
        };

        return string.Join('\n', lines);
    }

    private static string BuildTaskDeadlineChangeDecisionBody(
        TaskJournalItem task,
        TaskJournalDeadlineChangeRequest request,
        string actionText,
        string actorName,
        TimeZoneInfo timeZone)
    {
        var requestedDueAt = FormatTaskDueAt(request.RequestedDueAt, timeZone);
        var decisionDueAt = request.DecisionDueAt.HasValue
            ? FormatTaskDueAt(request.DecisionDueAt.Value, timeZone)
            : FormatTaskDueAt(task.DueAt, timeZone);
        var lines = new List<string>
        {
            $"{actorName} {actionText}.",
            string.Empty,
            $"Клиент/дело: {task.ObjectName}",
            $"Задача: {task.Title}",
            $"Запрашивал(а): {FormatNotificationName(request.RequestedByName)}",
            $"Запрошенный срок: {requestedDueAt}",
        };

        if (string.Equals(request.Status, "approved", StringComparison.OrdinalIgnoreCase))
        {
            lines.Add($"Новый срок: {decisionDueAt}");
        }
        else
        {
            lines.Add($"Текущий срок остался: {decisionDueAt}");
        }

        if (!string.IsNullOrWhiteSpace(request.DecisionComment))
        {
            lines.Add(string.Empty);
            lines.Add($"Комментарий: {request.DecisionComment.Trim()}");
        }

        return string.Join('\n', lines);
    }

    private static string BuildTaskApprovalOverdueBody(
        TaskJournalItem task,
        TaskJournalParticipant approver,
        TimeZoneInfo timeZone)
    {
        var dueText = task.ApprovalDueAt.HasValue
            ? FormatTaskDueAt(DateTime.SpecifyKind(task.ApprovalDueAt.Value, DateTimeKind.Utc), timeZone)
            : "не указан";
        var taskDueText = FormatTaskDueAt(task.DueAt, timeZone);
        var lines = new List<string>
        {
            "Срок согласования задачи истек.",
            string.Empty,
            $"Клиент/дело: {task.ObjectName}",
            $"Задача: {task.Title}",
            $"Срок согласования: {dueText}",
            $"Срок задачи: {taskDueText}",
        };

        var approverName = ResolveParticipantName(approver);
        if (!string.IsNullOrWhiteSpace(approverName))
        {
            lines.Add($"Текущий согласующий: {approverName}");
        }

        if (!string.IsNullOrWhiteSpace(task.AssigneeName))
        {
            lines.Add($"Исполнитель: {task.AssigneeName.Trim()}");
        }

        return string.Join('\n', lines);
    }

    private static string BuildTaskChangedBody(
        TaskJournalItem task,
        IReadOnlyList<string> changeLabels,
        string actorName,
        string? comment,
        TimeZoneInfo timeZone)
    {
        var dueText = FormatTaskDueAt(task.DueAt, timeZone);
        var lines = new List<string>();

        if (changeLabels.Count > 0)
        {
            lines.Add("Изменения:");
            foreach (var changeLabel in OrderTaskChangeLabels(changeLabels))
            {
                lines.Add($"- {changeLabel}");
            }
            lines.Add(string.Empty);
        }

        lines.Add($"Задача: {task.Title}");
        lines.Add($"Автор изменения: {actorName}");
        lines.Add($"Клиент/дело: {task.ObjectName}");
        lines.Add($"Текущий срок: {dueText}");

        if (!string.IsNullOrWhiteSpace(comment))
        {
            lines.Add(string.Empty);
            lines.Add($"Комментарий: {comment.Trim()}");
        }

        return string.Join('\n', lines);
    }

    private static bool IsImportantTaskChangeLabel(string label)
        => label.StartsWith("Срок:", StringComparison.OrdinalIgnoreCase)
           || label.StartsWith("Постановщик:", StringComparison.OrdinalIgnoreCase)
           || label.StartsWith("Исполнитель:", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> OrderTaskChangeLabels(IReadOnlyList<string> labels)
        => labels
            .Select((label, index) => new { label, index })
            .OrderBy(item => IsImportantTaskChangeLabel(item.label) ? 0 : 1)
            .ThenBy(item => item.index)
            .Select(item => item.label);

    private static void AppendTaskChangeDetails(List<string> lines, IReadOnlyList<string> changeLabels, string? comment)
    {
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
    }

    private static string FormatNotificationName(string? value)
        => string.IsNullOrWhiteSpace(value) ? "не указан" : value.Trim();

    private static string NormalizeNotificationText(string? value, string fallback)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return Truncate(normalized, 500);
    }

    private static string BuildStickyNotePreview(string? value)
    {
        var normalized = string.Join(' ', (value ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(normalized)
            ? "Без текста"
            : Truncate(normalized, 300);
    }

    private static string FormatStickyNoteReminderAt(DateTime? reminderAt, TimeZoneInfo timeZone)
        => reminderAt.HasValue
            ? FormatTaskDueAt(DateTime.SpecifyKind(reminderAt.Value, DateTimeKind.Utc), timeZone)
            : "без срока";

    private static string FormatStickyNoteEventDate(DateTime? reminderAt)
        => reminderAt.HasValue
            ? DateTime.SpecifyKind(reminderAt.Value, DateTimeKind.Utc).ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture)
            : "no-date";

    private static string NormalizeStickyNoteImportantAction(string? action)
        => NormalizeStickyNoteImportantActionKey(action) switch
        {
            "pinned" => "закрепил(а) важную заметку",
            "updated" => "изменил(а) важную заметку",
            "unpinned" => "открепил(а) важную заметку",
            "deleted" => "удалил(а) важную заметку",
            _ => "изменил(а) важную заметку"
        };

    private static string NormalizeStickyNoteImportantActionKey(string? action)
    {
        var normalized = action?.Trim().ToLowerInvariant();
        return normalized is "pinned" or "updated" or "unpinned" or "deleted"
            ? normalized
            : "updated";
    }

    private static string BuildStableTextHash(string? value)
    {
        var normalized = value ?? string.Empty;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private async Task<IReadOnlyList<int>> ResolveVisiblePaymentNotificationRecipientIdsAsync(
        Payment payment,
        string section,
        IReadOnlyCollection<int> recipientUserIds,
        int? actorUserId,
        CancellationToken cancellationToken)
        => await ResolveVisibleDataScopedNotificationRecipientIdsAsync(
            recipientUserIds,
            actorUserId,
            section,
            async (access, ct) => await _context.Payments
                .AsNoTracking()
                .Where(item => item.Id == payment.Id)
                .ApplyPaymentVisibility(access)
                .AnyAsync(ct),
            cancellationToken);

    private async Task<IReadOnlyList<int>> ResolveVisibleActNotificationRecipientIdsAsync(
        Act act,
        IReadOnlyCollection<int> recipientUserIds,
        int? actorUserId,
        CancellationToken cancellationToken)
        => await ResolveVisibleDataScopedNotificationRecipientIdsAsync(
            recipientUserIds,
            actorUserId,
            RolePermissionCatalog.Acts,
            async (access, ct) => await _context.Acts
                .AsNoTracking()
                .Where(item => item.Id == act.Id)
                .ApplyActVisibility(access)
                .AnyAsync(ct),
            cancellationToken);

    private async Task<IReadOnlyList<int>> ResolveVisibleContractNotificationRecipientIdsAsync(
        Contract contract,
        IReadOnlyCollection<int> recipientUserIds,
        int? actorUserId,
        CancellationToken cancellationToken)
        => await ResolveVisibleDataScopedNotificationRecipientIdsAsync(
            recipientUserIds,
            actorUserId,
            RolePermissionCatalog.Contracts,
            async (access, ct) => await _context.Contracts
                .AsNoTracking()
                .Where(item => item.Id == contract.Id)
                .ApplyContractVisibility(access)
                .AnyAsync(ct),
            cancellationToken);

    private async Task<IReadOnlyCollection<int>> IncludeConfiguredCourtLeaderAsync(
        IReadOnlyCollection<int> recipientUserIds,
        bool includeLeader,
        CancellationToken cancellationToken)
    {
        if (!includeLeader) return recipientUserIds;
        var configuredValue = await _context.SystemSettings.AsNoTracking()
            .Where(setting => setting.Key == CourtEventLeaderUserIdSettingKey)
            .Select(setting => setting.Value)
            .FirstOrDefaultAsync(cancellationToken);
        if (!int.TryParse(configuredValue, out var leaderUserId) || leaderUserId <= 0 || recipientUserIds.Contains(leaderUserId))
            return recipientUserIds;
        return recipientUserIds.Append(leaderUserId).ToArray();
    }

    private async Task<IReadOnlyList<int>> ResolveVisibleCaseNotificationRecipientIdsAsync(
        ClientCase clientCase,
        IReadOnlyCollection<int> recipientUserIds,
        int? actorUserId,
        CancellationToken cancellationToken)
        => await ResolveVisibleDataScopedNotificationRecipientIdsAsync(
            recipientUserIds,
            actorUserId,
            RolePermissionCatalog.Cases,
            async (access, ct) => await _context.ClientCases
                .AsNoTracking()
                .Where(item => item.Id == clientCase.Id)
                .ApplyClientCaseVisibility(access)
                .AnyAsync(ct),
            cancellationToken);

    private async Task<IReadOnlyList<int>> ResolveVisibleDataScopedNotificationRecipientIdsAsync(
        IReadOnlyCollection<int> recipientUserIds,
        int? actorUserId,
        string section,
        Func<DataVisibilityAccess, CancellationToken, Task<bool>> canSeeObjectAsync,
        CancellationToken cancellationToken)
    {
        var candidateUserIds = recipientUserIds
            .Where(userId => userId > 0 && !UserNotificationAntiSpamPolicy.ShouldSuppressSelfNotification(userId, actorUserId))
            .Distinct()
            .ToArray();
        if (candidateUserIds.Length == 0)
        {
            return Array.Empty<int>();
        }

        var recipientAccessRows = await _context.Users
            .AsNoTracking()
            .Where(user => candidateUserIds.Contains(user.Id)
                && user.IsActive
                && user.IsApproved)
            .Select(user => new
            {
                user.Id,
                user.DepartmentId,
                user.Department,
                RoleName = user.Role != null ? user.Role.Name : string.Empty,
                Permission = user.Role == null
                    ? null
                    : user.Role.Permissions
                        .Where(permission => permission.Section == section)
                        .Select(permission => new
                        {
                            permission.CanView,
                            permission.ViewScope,
                        })
                        .FirstOrDefault(),
                HasAnyRolePermission = user.Role != null && user.Role.Permissions.Any(),
            })
            .ToListAsync(cancellationToken);
        if (recipientAccessRows.Count == 0)
        {
            return Array.Empty<int>();
        }

        var accessCandidates = new List<NotificationRecipientAccessCandidate>(recipientAccessRows.Count);
        foreach (var row in recipientAccessRows)
        {
            var isAdmin = string.Equals(row.RoleName, "admin", StringComparison.OrdinalIgnoreCase);
            if (!isAdmin && row.Permission is null && row.HasAnyRolePermission)
            {
                continue;
            }

            if (!isAdmin && row.Permission is not null && !row.Permission.CanView)
            {
                continue;
            }

            var scope = isAdmin || !RolePermissionCatalog.HasDataScope(section)
                ? DataVisibilityScope.All
                : DataVisibilityScope.Normalize(row.Permission?.ViewScope, DataVisibilityScope.Own);
            accessCandidates.Add(new NotificationRecipientAccessCandidate(
                row.Id,
                isAdmin,
                scope,
                row.DepartmentId,
                row.Department?.Trim()));
        }

        if (accessCandidates.Count == 0)
        {
            return Array.Empty<int>();
        }

        var departmentIds = accessCandidates
            .Where(candidate => string.Equals(candidate.Scope, DataVisibilityScope.Department, StringComparison.OrdinalIgnoreCase))
            .Select(candidate => candidate.DepartmentId)
            .OfType<int>()
            .Distinct()
            .ToArray();
        var departmentNames = accessCandidates
            .Where(candidate => string.Equals(candidate.Scope, DataVisibilityScope.Department, StringComparison.OrdinalIgnoreCase))
            .Select(candidate => candidate.Department)
            .Where(department => !string.IsNullOrWhiteSpace(department))
            .Select(department => department!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var departmentUsers = new List<NotificationDepartmentUser>();
        if (departmentIds.Length > 0 || departmentNames.Length > 0)
        {
            var departmentUserRows = await _context.Users
                .AsNoTracking()
                .Where(user => user.IsActive
                    && ((user.DepartmentId.HasValue && departmentIds.Contains(user.DepartmentId.Value))
                        || (user.Department != null && departmentNames.Contains(user.Department))))
                .Select(user => new { user.Id, user.DepartmentId, user.Department })
                .ToListAsync(cancellationToken);
            departmentUsers.AddRange(departmentUserRows.Select(user => new NotificationDepartmentUser(
                user.Id,
                user.DepartmentId,
                user.Department)));
        }

        var recipientVisibilityKeys = new List<(int UserId, string VisibilityKey)>(accessCandidates.Count);
        var visibilityByKey = new Dictionary<string, DataVisibilityAccess>(StringComparer.Ordinal);
        foreach (var candidate in accessCandidates)
        {
            IReadOnlyCollection<int> visibleUserIds;
            if (candidate.IsAdmin
                || string.Equals(candidate.Scope, DataVisibilityScope.All, StringComparison.OrdinalIgnoreCase))
            {
                visibleUserIds = Array.Empty<int>();
            }
            else if (string.Equals(candidate.Scope, DataVisibilityScope.Department, StringComparison.OrdinalIgnoreCase)
                     && (candidate.DepartmentId.HasValue || !string.IsNullOrWhiteSpace(candidate.Department)))
            {
                visibleUserIds = departmentUsers
                    .Where(user =>
                        (candidate.DepartmentId.HasValue
                            && user.DepartmentId == candidate.DepartmentId.Value)
                        || (!string.IsNullOrWhiteSpace(candidate.Department)
                            && string.Equals(user.Department, candidate.Department, StringComparison.Ordinal)))
                    .Select(user => user.UserId)
                    .Append(candidate.UserId)
                    .Distinct()
                    .ToArray();
            }
            else
            {
                visibleUserIds = [candidate.UserId];
            }

            var access = new DataVisibilityAccess(
                candidate.UserId,
                candidate.IsAdmin,
                section,
                candidate.Scope,
                visibleUserIds);
            var visibilityKey = BuildDataVisibilityGroupKey(access);
            recipientVisibilityKeys.Add((candidate.UserId, visibilityKey));
            visibilityByKey.TryAdd(visibilityKey, access);
        }

        var visibleKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (visibilityKey, access) in visibilityByKey)
        {
            if (await canSeeObjectAsync(access, cancellationToken))
            {
                visibleKeys.Add(visibilityKey);
            }
        }

        return recipientVisibilityKeys
            .Where(item => visibleKeys.Contains(item.VisibilityKey))
            .Select(item => item.UserId)
            .ToArray();
    }

    private sealed record NotificationRecipientAccessCandidate(
        int UserId,
        bool IsAdmin,
        string Scope,
        int? DepartmentId,
        string? Department);

    private sealed record NotificationDepartmentUser(int UserId, int? DepartmentId, string? Department);

    private static string BuildDataVisibilityGroupKey(DataVisibilityAccess access)
        => access.SeesAll
            ? "all"
            : $"restricted:{string.Join(',', access.VisibleUserIds.Distinct().Order())}";

    private static ClaimsPrincipal CreateNotificationRecipientPrincipal(int userId)
        => new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString(CultureInfo.InvariantCulture))],
            authenticationType: "notification-service"));

    private static Dictionary<string, string?> BuildPaymentTemplateValues(
        Payment payment,
        string actorName,
        IReadOnlyList<string> changeLabels)
        => new()
        {
            ["actor.name"] = actorName,
            ["payment.title"] = BuildPaymentTitle(payment),
            ["payment.date"] = FormatPaymentDate(payment.Date),
            ["payment.amount"] = payment.Amount.ToString("N2", RuCulture),
            ["payment.status"] = FormatPaymentStatus(payment.Status),
            ["payment.changes"] = changeLabels.Count == 0
                ? string.Empty
                : string.Join('\n', changeLabels.Select(label => $"- {label}"))
        };

    private static string BuildPaymentAssignedBody(Payment payment, string actorName)
        => NotificationTemplateCatalog.CompactBlankLines(
            $"{actorName} назначил(а) вам платеж: {BuildPaymentTitle(payment)}\n\nДата: {FormatPaymentDate(payment.Date)}\nСумма: {payment.Amount.ToString("N2", RuCulture)}\nСтатус: {FormatPaymentStatus(payment.Status)}");

    private static string BuildPaymentChangedBody(
        Payment payment,
        string actorName,
        IReadOnlyList<string> changeLabels)
        => NotificationTemplateCatalog.CompactBlankLines(
            $"{actorName} изменил(а) платеж: {BuildPaymentTitle(payment)}\n\nИзменения:\n{string.Join('\n', changeLabels.Select(label => $"- {label}"))}\n\nДата: {FormatPaymentDate(payment.Date)}\nСумма: {payment.Amount.ToString("N2", RuCulture)}\nСтатус: {FormatPaymentStatus(payment.Status)}");

    private static string BuildPaymentTitle(Payment payment)
    {
        var description = NormalizeNotificationText(payment.Description, "Платеж");
        if (!string.Equals(description, "Платеж", StringComparison.OrdinalIgnoreCase))
        {
            return description;
        }

        return payment.Id > 0
            ? $"Платеж #{payment.Id}"
            : "Платеж";
    }

    private static string FormatPaymentDate(DateTime date)
        => date == default ? "не указана" : date.ToString("dd.MM.yyyy", RuCulture);

    private static string FormatPaymentStatus(PaymentStatus status)
        => status switch
        {
            PaymentStatus.Pending => "Ожидается",
            PaymentStatus.Completed => "Выполнен",
            PaymentStatus.Overdue => "Просрочен",
            PaymentStatus.Processing => "Частично оплачено",
            PaymentStatus.Cancelled => "Отменён",
            _ => status.ToString()
        };

    private static IReadOnlyList<string> NormalizePaymentChangeLabels(IReadOnlyList<string> changeLabels)
        => changeLabels
            .Select(label => NormalizeNotificationText(label, string.Empty))
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();

    private static Dictionary<string, string?> BuildInvoiceTemplateValues(
        Payment invoicePayment,
        string actorName,
        IReadOnlyList<string> changeLabels)
        => new()
        {
            ["actor.name"] = actorName,
            ["invoice.number"] = BuildInvoiceNumber(invoicePayment),
            ["invoice.title"] = BuildInvoiceTitle(invoicePayment),
            ["invoice.date"] = FormatPaymentDate(invoicePayment.AccountDate ?? invoicePayment.Invoice?.Date ?? invoicePayment.Date),
            ["invoice.dueDate"] = FormatPaymentDate(invoicePayment.Date),
            ["invoice.amount"] = invoicePayment.Amount.ToString("N2", RuCulture),
            ["invoice.status"] = FormatPaymentStatus(invoicePayment.Status),
            ["invoice.workflowStatus"] = FormatInvoiceWorkflowStatus(invoicePayment.InvoiceWorkflowStatus),
            ["invoice.changes"] = changeLabels.Count == 0
                ? string.Empty
                : string.Join('\n', changeLabels.Select(label => $"- {label}"))
        };

    private static string BuildInvoiceCreatedBody(Payment invoicePayment, string actorName)
        => NotificationTemplateCatalog.CompactBlankLines(
            $"{actorName} создал(а) счет №{BuildInvoiceNumber(invoicePayment)}: {BuildInvoiceTitle(invoicePayment)}\n\nДата счета: {FormatPaymentDate(invoicePayment.AccountDate ?? invoicePayment.Invoice?.Date ?? invoicePayment.Date)}\nСрок оплаты: {FormatPaymentDate(invoicePayment.Date)}\nСумма: {invoicePayment.Amount.ToString("N2", RuCulture)}\nСтатус: {FormatInvoiceWorkflowStatus(invoicePayment.InvoiceWorkflowStatus)}");

    private static string BuildInvoiceChangedBody(
        Payment invoicePayment,
        string actorName,
        IReadOnlyList<string> changeLabels)
        => NotificationTemplateCatalog.CompactBlankLines(
            $"{actorName} изменил(а) счет №{BuildInvoiceNumber(invoicePayment)}: {BuildInvoiceTitle(invoicePayment)}\n\nИзменения:\n{string.Join('\n', changeLabels.Select(label => $"- {label}"))}\n\nСрок оплаты: {FormatPaymentDate(invoicePayment.Date)}\nСумма: {invoicePayment.Amount.ToString("N2", RuCulture)}\nСтатус: {FormatInvoiceWorkflowStatus(invoicePayment.InvoiceWorkflowStatus)}");

    private static string BuildInvoiceNumber(Payment invoicePayment)
        => NormalizeNotificationText(invoicePayment.Invoice?.Number ?? invoicePayment.Account, invoicePayment.Id > 0 ? invoicePayment.Id.ToString(CultureInfo.InvariantCulture) : "без номера");

    private static string BuildInvoiceTitle(Payment invoicePayment)
        => NormalizeNotificationText(invoicePayment.Description, "Счет");

    private static string FormatInvoiceWorkflowStatus(string? status)
        => (InvoiceWorkflowStatus.Normalize(status) ?? status)?.Trim() switch
        {
            InvoiceWorkflowStatus.Draft => "Черновик",
            InvoiceWorkflowStatus.NotSigned => "Не подписан",
            InvoiceWorkflowStatus.RequiresClientSignature => "Требует подписи клиента",
            InvoiceWorkflowStatus.OnApproval => "На согласовании",
            InvoiceWorkflowStatus.Approved => "Согласован",
            InvoiceWorkflowStatus.ApprovalRejected => "Согласование отклонено",
            InvoiceWorkflowStatus.NeedsCorrection => "Требует корректировки",
            InvoiceWorkflowStatus.OnSigning => "Отправлен на подпись",
            InvoiceWorkflowStatus.SignedByClient => "Подписан клиентом",
            InvoiceWorkflowStatus.Paid => "Оплачен",
            InvoiceWorkflowStatus.PartiallyPaid => "Частично оплачен",
            InvoiceWorkflowStatus.Overdue => "Просрочен",
            InvoiceWorkflowStatus.Cancelled => "Отменен",
            _ => "не указан"
        };

    private static Dictionary<string, string?> BuildActTemplateValues(
        Act act,
        string actorName,
        IReadOnlyList<string> changeLabels)
        => new()
        {
            ["actor.name"] = actorName,
            ["act.number"] = BuildActNumber(act),
            ["act.title"] = BuildActTitle(act),
            ["act.date"] = FormatPaymentDate(act.Date),
            ["act.amount"] = act.Amount.ToString("N2", RuCulture),
            ["act.status"] = FormatActStatus(act.Status),
            ["act.workflowStatus"] = FormatActWorkflowStatus(act.WorkflowStatus),
            ["act.changes"] = changeLabels.Count == 0
                ? string.Empty
                : string.Join('\n', changeLabels.Select(label => $"- {label}"))
        };

    private static string BuildActCreatedBody(Act act, string actorName)
        => NotificationTemplateCatalog.CompactBlankLines(
            $"{actorName} создал(а) акт №{BuildActNumber(act)}: {BuildActTitle(act)}\n\nДата акта: {FormatPaymentDate(act.Date)}\nСумма: {act.Amount.ToString("N2", RuCulture)}\nСтатус: {FormatActWorkflowStatus(act.WorkflowStatus)}");

    private static string BuildActChangedBody(
        Act act,
        string actorName,
        IReadOnlyList<string> changeLabels)
        => NotificationTemplateCatalog.CompactBlankLines(
            $"{actorName} изменил(а) акт №{BuildActNumber(act)}: {BuildActTitle(act)}\n\nИзменения:\n{string.Join('\n', changeLabels.Select(label => $"- {label}"))}\n\nДата акта: {FormatPaymentDate(act.Date)}\nСумма: {act.Amount.ToString("N2", RuCulture)}\nСтатус: {FormatActWorkflowStatus(act.WorkflowStatus)}");

    private static string BuildActNumber(Act act)
        => NormalizeNotificationText(act.Number, act.Id > 0 ? act.Id.ToString(CultureInfo.InvariantCulture) : "без номера");

    private static string BuildActTitle(Act act)
        => NormalizeNotificationText(act.Title, "Акт");

    private static string FormatActStatus(ActStatus status)
        => status switch
        {
            ActStatus.Created => "Создан",
            ActStatus.Transferred => "Передан",
            ActStatus.Signed => "Подписан",
            ActStatus.Terminated => "Отменен",
            _ => status.ToString()
        };

    private static string FormatActWorkflowStatus(string? status)
        => (ActWorkflowStatus.Normalize(status) ?? status)?.Trim() switch
        {
            ActWorkflowStatus.Draft => "Черновик",
            ActWorkflowStatus.NotSigned => "Не подписан",
            ActWorkflowStatus.RequiresClientSignature => "Требует подписи клиента",
            ActWorkflowStatus.OnApproval => "На согласовании",
            ActWorkflowStatus.Approved => "Согласован",
            ActWorkflowStatus.ApprovalRejected => "Согласование отклонено",
            ActWorkflowStatus.NeedsCorrection => "Требует корректировки",
            ActWorkflowStatus.OnSigning => "Отправлен на подпись",
            ActWorkflowStatus.Signed => "Подписан",
            ActWorkflowStatus.Cancelled => "Отменен",
            _ => "не указан"
        };

    private static Dictionary<string, string?> BuildContractTemplateValues(
        Contract contract,
        string actorName,
        IReadOnlyList<string> changeLabels,
        int? daysLeft)
        => new()
        {
            ["actor.name"] = actorName,
            ["contract.number"] = BuildContractNumber(contract),
            ["contract.title"] = BuildContractTitle(contract),
            ["contract.date"] = FormatPaymentDate(contract.Date),
            ["contract.startDate"] = FormatNullableDate(contract.StartDate),
            ["contract.validUntil"] = contract.IsOpenEnded ? "бессрочный" : FormatNullableDate(contract.ValidUntil),
            ["contract.amount"] = contract.Amount.HasValue ? contract.Amount.Value.ToString("N2", RuCulture) : "не указана",
            ["contract.status"] = FormatContractStatus(contract.Status),
            ["contract.changes"] = changeLabels.Count == 0
                ? string.Empty
                : string.Join('\n', changeLabels.Select(label => $"- {label}")),
            ["contract.daysLeft"] = daysLeft?.ToString(CultureInfo.InvariantCulture) ?? string.Empty
        };

    private static string BuildContractCreatedBody(Contract contract, string actorName)
        => NotificationTemplateCatalog.CompactBlankLines(
            $"{actorName} создал(а) договор №{BuildContractNumber(contract)}: {BuildContractTitle(contract)}\n\nДата договора: {FormatPaymentDate(contract.Date)}\nСрок действия: {(contract.IsOpenEnded ? "бессрочный" : FormatNullableDate(contract.ValidUntil))}\nСумма: {(contract.Amount.HasValue ? contract.Amount.Value.ToString("N2", RuCulture) : "не указана")}\nСтатус: {FormatContractStatus(contract.Status)}");

    private static string BuildContractChangedBody(
        Contract contract,
        string actorName,
        IReadOnlyList<string> changeLabels)
        => NotificationTemplateCatalog.CompactBlankLines(
            $"{actorName} изменил(а) договор №{BuildContractNumber(contract)}: {BuildContractTitle(contract)}\n\nИзменения:\n{string.Join('\n', changeLabels.Select(label => $"- {label}"))}\n\nСрок действия: {(contract.IsOpenEnded ? "бессрочный" : FormatNullableDate(contract.ValidUntil))}\nСумма: {(contract.Amount.HasValue ? contract.Amount.Value.ToString("N2", RuCulture) : "не указана")}\nСтатус: {FormatContractStatus(contract.Status)}");

    private static string BuildContractExpiringBody(Contract contract, int daysLeft)
        => NotificationTemplateCatalog.CompactBlankLines(
            $"Срок договора №{BuildContractNumber(contract)} подходит к концу.\n\nДоговор: {BuildContractTitle(contract)}\nДата окончания: {FormatNullableDate(contract.ValidUntil)}\nОсталось дней: {daysLeft.ToString(CultureInfo.InvariantCulture)}\nСтатус: {FormatContractStatus(contract.Status)}");

    private static string BuildContractNumber(Contract contract)
        => NormalizeNotificationText(contract.Number, contract.Id > 0 ? contract.Id.ToString(CultureInfo.InvariantCulture) : "без номера");

    private static string BuildContractTitle(Contract contract)
        => NormalizeNotificationText(contract.Title, "Договор");

    private static string FormatNullableDate(DateTime? date)
        => date.HasValue ? FormatPaymentDate(date.Value) : "не указана";

    private static string FormatContractStatus(string? status)
        => (ContractStatus.Normalize(status) ?? status)?.Trim() switch
        {
            ContractStatus.Draft => "Черновик",
            ContractStatus.NotSigned => "Не подписан",
            ContractStatus.RequiresClientSignature => "Требует подписи клиента",
            ContractStatus.OnApproval => "На согласовании",
            ContractStatus.Approved => "Согласован",
            ContractStatus.ApprovalRejected => "Согласование отклонено",
            ContractStatus.NeedsCorrection => "Требует корректировки",
            ContractStatus.OnSigning => "Отправлен на подпись",
            ContractStatus.Concluded => "Заключен",
            ContractStatus.Terminated => "Расторгнут",
            ContractStatus.Cancelled => "Аннулирован",
            _ => "не указан"
        };

    private static Dictionary<string, string?> BuildCourtEventTemplateValues(
        ClientCase clientCase,
        ClientCaseCourtEvent courtEvent,
        string action,
        string actorName,
        TimeZoneInfo recipientTimeZone)
        => new()
        {
            ["actor.name"] = actorName,
            ["case.title"] = BuildClientCaseTitle(clientCase),
            ["case.number"] = NormalizeNotificationText(clientCase.CaseNumber, clientCase.Id.ToString(CultureInfo.InvariantCulture)),
            ["courtEvent.title"] = NormalizeNotificationText(courtEvent.Title, "Судебное событие"),
            ["courtEvent.action"] = ResolveCourtEventActionText(action),
            ["courtEvent.scheduledAt"] = FormatCourtEventDate(courtEvent.ScheduledAt, courtEvent.AllDay, recipientTimeZone),
            ["courtEvent.courtName"] = NormalizeNotificationText(courtEvent.CourtName, "не указан"),
            ["courtEvent.courtCaseNumber"] = NormalizeNotificationText(courtEvent.CourtCaseNumber, "не указан"),
            ["courtEvent.courtroom"] = NormalizeNotificationText(courtEvent.Courtroom, "не указан"),
            ["courtEvent.judgeName"] = NormalizeNotificationText(courtEvent.JudgeName, "не указан"),
            ["courtEvent.status"] = FormatCourtMovementStatus(courtEvent.MovementStatus),
            ["courtEvent.result"] = NormalizeNotificationText(courtEvent.ResultSummary, "не указан"),
            ["courtEvent.sourceType"] = NormalizeNotificationText(courtEvent.SourceType, "CRM")
        };

    private static Dictionary<string, string?> BuildCourtSyncFailedTemplateValues(
        ClientCase clientCase,
        string providerName,
        string summary,
        string actorName)
        => new()
        {
            ["actor.name"] = actorName,
            ["case.title"] = BuildClientCaseTitle(clientCase),
            ["case.number"] = NormalizeNotificationText(clientCase.CaseNumber, clientCase.Id.ToString(CultureInfo.InvariantCulture)),
            ["courtEvent.title"] = "Ошибка судебной синхронизации",
            ["courtEvent.action"] = "не смог(ла) проверить",
            ["courtEvent.scheduledAt"] = "не указана",
            ["courtEvent.courtName"] = NormalizeNotificationText(clientCase.CourtName, providerName),
            ["courtEvent.courtCaseNumber"] = NormalizeNotificationText(clientCase.CourtCaseNumber, "не указан"),
            ["courtEvent.courtroom"] = "не указан",
            ["courtEvent.judgeName"] = NormalizeNotificationText(clientCase.JudgeName, "не указан"),
            ["courtEvent.status"] = "ошибка проверки",
            ["courtEvent.result"] = summary,
            ["courtEvent.sourceType"] = providerName
        };

    private static Dictionary<string, string?> BuildCaseProceduralDeadlineTemplateValues(
        ClientCase clientCase,
        ClientCaseProceduralDeadline deadline,
        string action,
        string actorName,
        TimeZoneInfo? recipientTimeZone = null)
        => new()
        {
            ["actor.name"] = actorName,
            ["case.title"] = BuildClientCaseTitle(clientCase),
            ["case.number"] = NormalizeNotificationText(clientCase.CaseNumber, clientCase.Id.ToString(CultureInfo.InvariantCulture)),
            ["deadline.title"] = NormalizeNotificationText(deadline.Title, "Процессуальный срок"),
            ["deadline.type"] = NormalizeNotificationText(deadline.DeadlineType, "не указан"),
            ["deadline.action"] = ResolveCaseProceduralDeadlineActionText(action),
            ["deadline.dueAt"] = recipientTimeZone is null
                ? FormatCourtEventDate(deadline.DueAt, deadline.AllDay)
                : FormatCourtEventDate(deadline.DueAt, deadline.AllDay, recipientTimeZone),
            ["deadline.status"] = FormatCaseProceduralDeadlineStatus(deadline.Status),
            ["deadline.warningDaysBefore"] = Math.Max(deadline.WarningDaysBefore, 0).ToString(CultureInfo.InvariantCulture),
            ["deadline.sourceType"] = NormalizeNotificationText(deadline.SourceType, "CRM"),
            ["deadline.notes"] = NormalizeNotificationText(deadline.Notes, "нет")
        };

    private static Dictionary<string, string?> BuildCaseDocumentTemplateValues(
        ClientCase clientCase,
        ClientCaseDocument document,
        string action,
        string actorName,
        bool requiresAction)
        => new()
        {
            ["actor.name"] = actorName,
            ["case.title"] = BuildClientCaseTitle(clientCase),
            ["case.number"] = NormalizeNotificationText(clientCase.CaseNumber, clientCase.Id.ToString(CultureInfo.InvariantCulture)),
            ["document.fileName"] = NormalizeNotificationText(document.OriginalFileName, "Документ"),
            ["document.type"] = NormalizeNotificationText(document.DocumentType, "не указан"),
            ["document.status"] = NormalizeNotificationText(document.Status, "не указан"),
            ["document.action"] = ResolveCaseDocumentActionText(action, requiresAction),
            ["document.version"] = document.Version.ToString(CultureInfo.InvariantCulture),
            ["document.size"] = FormatFileSize(document.SizeBytes),
            ["document.requiresAction"] = requiresAction ? "да" : "нет"
        };

    private static Dictionary<string, string?> BuildCaseConversationTemplateValues(
        ClientCase clientCase,
        string messageText,
        int attachmentCount,
        string actorName,
        DateTime occurredAtUtc)
        => new()
        {
            ["actor.name"] = actorName,
            ["case.title"] = BuildClientCaseTitle(clientCase),
            ["case.number"] = NormalizeNotificationText(clientCase.CaseNumber, clientCase.Id.ToString(CultureInfo.InvariantCulture)),
            ["conversation.preview"] = Truncate(messageText, 500),
            ["conversation.attachmentCount"] = Math.Max(attachmentCount, 0).ToString(CultureInfo.InvariantCulture),
            ["conversation.hasAttachments"] = attachmentCount > 0 ? "да" : "нет",
            ["conversation.occurredAt"] = FormatCourtEventDate(occurredAtUtc, allDay: false)
        };

    private static Dictionary<string, string?> BuildCaseAccessTemplateValues(
        ClientCase clientCase,
        string requesterName,
        string reason,
        string? comment,
        string decision,
        DateTime? requestedAt,
        DateTime? resolvedAt,
        string? actorName = null)
        => new()
        {
            ["actor.name"] = NormalizeNotificationText(actorName, "Сотрудник"),
            ["case.title"] = BuildClientCaseTitle(clientCase),
            ["case.number"] = NormalizeNotificationText(clientCase.CaseNumber, clientCase.Id.ToString(CultureInfo.InvariantCulture)),
            ["case.ownerName"] = BuildCaseOwnerName(clientCase),
            ["access.requesterName"] = requesterName,
            ["access.reason"] = reason,
            ["access.comment"] = NormalizeNotificationText(comment, "нет"),
            ["access.decision"] = decision,
            ["access.requestedAt"] = requestedAt.HasValue ? FormatCourtEventDate(requestedAt.Value, allDay: false) : "не указана",
            ["access.resolvedAt"] = resolvedAt.HasValue ? FormatCourtEventDate(resolvedAt.Value, allDay: false) : "не указана"
        };

    private static string BuildCourtSyncFailedBody(ClientCase clientCase, string providerName, string summary)
        => NotificationTemplateCatalog.CompactBlankLines(
            $"Судебный источник по делу {BuildClientCaseTitle(clientCase)} не удалось проверить.\n\nИсточник: {providerName}\nСуд: {NormalizeNotificationText(clientCase.CourtName, "не указан")}\nНомер в суде: {NormalizeNotificationText(clientCase.CourtCaseNumber, "не указан")}\nЧто произошло: {summary}");

    private static string BuildCaseProceduralDeadlineSubject(
        ClientCase clientCase,
        ClientCaseProceduralDeadline deadline,
        string action)
        => NormalizeCaseProceduralDeadlineAction(action) switch
        {
            "created" => $"Новый процессуальный срок: {BuildClientCaseTitle(clientCase)}",
            "closed" => $"Закрыт процессуальный срок: {BuildClientCaseTitle(clientCase)}",
            "reminder" => $"Приближается процессуальный срок: {NormalizeNotificationText(deadline.Title, "Процессуальный срок")}",
            _ => $"Обновлен процессуальный срок: {BuildClientCaseTitle(clientCase)}"
        };

    private static string BuildCaseProceduralDeadlineBody(
        ClientCase clientCase,
        ClientCaseProceduralDeadline deadline,
        string action,
        string actorName,
        TimeZoneInfo? recipientTimeZone = null)
    {
        var normalizedAction = NormalizeCaseProceduralDeadlineAction(action);
        var intro = normalizedAction switch
        {
            "created" => $"{actorName} добавил(а) процессуальный срок по делу.",
            "closed" => $"{actorName} закрыл(а) процессуальный срок по делу.",
            "reminder" => "Приближается процессуальный срок по делу.",
            _ => $"{actorName} обновил(а) процессуальный срок по делу."
        };

        var builder = new StringBuilder()
            .Append(intro)
            .AppendLine()
            .AppendLine()
            .Append("Дело: ")
            .Append(BuildClientCaseTitle(clientCase))
            .AppendLine()
            .Append("Срок: ")
            .Append(NormalizeNotificationText(deadline.Title, "Процессуальный срок"))
            .AppendLine()
            .Append("Дата: ")
            .Append(recipientTimeZone is null
                ? FormatCourtEventDate(deadline.DueAt, deadline.AllDay)
                : FormatCourtEventDate(deadline.DueAt, deadline.AllDay, recipientTimeZone))
            .AppendLine()
            .Append("Статус: ")
            .Append(FormatCaseProceduralDeadlineStatus(deadline.Status))
            .AppendLine()
            .Append("Предупреждать за дней: ")
            .Append(Math.Max(deadline.WarningDaysBefore, 0).ToString(CultureInfo.InvariantCulture));

        if (!string.IsNullOrWhiteSpace(deadline.Notes))
        {
            builder.AppendLine().Append("Примечание: ").Append(deadline.Notes.Trim());
        }

        if (!string.IsNullOrWhiteSpace(deadline.CloseReason))
        {
            builder.AppendLine().Append("Причина закрытия: ").Append(deadline.CloseReason.Trim());
        }

        if (!string.IsNullOrWhiteSpace(deadline.SourceType) && deadline.SourceId.HasValue)
        {
            builder
                .AppendLine()
                .Append("Источник: ")
                .Append(deadline.SourceType.Trim())
                .Append(" #")
                .Append(deadline.SourceId.Value.ToString(CultureInfo.InvariantCulture));
        }

        return NotificationTemplateCatalog.CompactBlankLines(builder.ToString());
    }

    private static string BuildCaseDocumentSubject(
        ClientCase clientCase,
        ClientCaseDocument document,
        string action,
        bool requiresAction)
    {
        if (requiresAction)
        {
            return $"Документ требует внимания: {NormalizeNotificationText(document.OriginalFileName, "Документ")}";
        }

        return NormalizeCaseDocumentAction(action) switch
        {
            "uploaded" => $"Загружен документ: {BuildClientCaseTitle(clientCase)}",
            "version-created" => $"Новая версия документа: {BuildClientCaseTitle(clientCase)}",
            "generated" => $"Сформирован документ: {BuildClientCaseTitle(clientCase)}",
            "updated" => $"Обновлен документ: {BuildClientCaseTitle(clientCase)}",
            "deleted" => $"Удален документ: {BuildClientCaseTitle(clientCase)}",
            _ => $"Документ дела: {BuildClientCaseTitle(clientCase)}"
        };
    }

    private static string BuildCaseConversationSubject(ClientCase clientCase, int attachmentCount)
        => attachmentCount > 0
            ? $"Новое сообщение с вложением: {BuildClientCaseTitle(clientCase)}"
            : $"Новое сообщение по делу: {BuildClientCaseTitle(clientCase)}";

    private static string BuildCaseConversationBody(
        ClientCase clientCase,
        string messageText,
        int attachmentCount,
        string actorName,
        DateTime occurredAtUtc)
    {
        var builder = new StringBuilder()
            .Append(actorName)
            .Append(" написал(а) сообщение по делу.")
            .AppendLine()
            .AppendLine()
            .Append("Дело: ")
            .Append(BuildClientCaseTitle(clientCase))
            .AppendLine()
            .Append("Сообщение: ")
            .Append(Truncate(messageText, 700))
            .AppendLine()
            .Append("Вложений: ")
            .Append(Math.Max(attachmentCount, 0).ToString(CultureInfo.InvariantCulture))
            .AppendLine()
            .Append("Дата: ")
            .Append(FormatCourtEventDate(occurredAtUtc, allDay: false));

        return NotificationTemplateCatalog.CompactBlankLines(builder.ToString());
    }

    private static string BuildCaseDocumentBody(
        ClientCase clientCase,
        ClientCaseDocument document,
        string action,
        string actorName,
        bool requiresAction)
    {
        var builder = new StringBuilder()
            .Append(actorName)
            .Append(' ')
            .Append(ResolveCaseDocumentActionText(action, requiresAction))
            .Append(" по делу.")
            .AppendLine()
            .AppendLine()
            .Append("Дело: ")
            .Append(BuildClientCaseTitle(clientCase))
            .AppendLine()
            .Append("Документ: ")
            .Append(NormalizeNotificationText(document.OriginalFileName, "Документ"))
            .AppendLine()
            .Append("Тип: ")
            .Append(NormalizeNotificationText(document.DocumentType, "не указан"))
            .AppendLine()
            .Append("Статус: ")
            .Append(NormalizeNotificationText(document.Status, "не указан"))
            .AppendLine()
            .Append("Версия: ")
            .Append(document.Version.ToString(CultureInfo.InvariantCulture))
            .AppendLine()
            .Append("Размер: ")
            .Append(FormatFileSize(document.SizeBytes));

        if (requiresAction)
        {
            builder
                .AppendLine()
                .Append("Требуется действие по документу.");
        }

        return NotificationTemplateCatalog.CompactBlankLines(builder.ToString());
    }

    private static string BuildCaseAccessRequestBody(
        ClientCase clientCase,
        string requesterName,
        string reason,
        DateTime requestedAt)
        => NotificationTemplateCatalog.CompactBlankLines(
            $"Пользователь {requesterName} запросил доступ к скрытому делу.\n\nКонтрагент: {BuildCaseOwnerName(clientCase)}\nДело: {BuildClientCaseTitle(clientCase)}\nПричина: {reason}\nДата запроса: {FormatCourtEventDate(requestedAt, allDay: false)}");

    private static string BuildCaseAccessResolutionBody(
        ClientCase clientCase,
        bool granted,
        string actorName,
        string comment,
        DateTime resolvedAt)
        => NotificationTemplateCatalog.CompactBlankLines(
            $"{actorName} {(granted ? "разрешил(а) доступ к делу." : "отклонил(а) запрос доступа к делу.")}\n\nКонтрагент: {BuildCaseOwnerName(clientCase)}\nДело: {BuildClientCaseTitle(clientCase)}\nКомментарий: {comment}\nДата решения: {FormatCourtEventDate(resolvedAt, allDay: false)}");

    private static string BuildCourtEventSubject(ClientCase clientCase, ClientCaseCourtEvent courtEvent, string notificationType, string action)
        => string.Equals(action, "rescheduled", StringComparison.OrdinalIgnoreCase)
            ? $"Перенесено судебное заседание: {BuildClientCaseTitle(clientCase)}"
            : notificationType switch
            {
                UserNotificationTypes.CourtDecisionRecorded => $"Зафиксировано судебное решение: {BuildClientCaseTitle(clientCase)}",
                UserNotificationTypes.CourtDecisionPublished => $"Опубликован судебный акт: {BuildClientCaseTitle(clientCase)}",
                UserNotificationTypes.CourtStatusChanged => $"Изменился судебный статус: {BuildClientCaseTitle(clientCase)}",
                _ => $"Судебное заседание: {BuildClientCaseTitle(clientCase)}"
            };

    private static string BuildCourtEventBody(
        ClientCase clientCase,
        ClientCaseCourtEvent courtEvent,
        string action,
        string actorName,
        string notificationType,
        TimeZoneInfo recipientTimeZone)
    {
        var actionText = ResolveCourtEventActionText(action);
        var intro = string.Equals(action, "rescheduled", StringComparison.OrdinalIgnoreCase)
            ? $"{actorName} перенёс(ла) судебное заседание по делу. Проверьте новую дату и подготовку."
            : notificationType switch
            {
                UserNotificationTypes.CourtDecisionRecorded => $"{actorName} {actionText} судебное решение по делу.",
                UserNotificationTypes.CourtDecisionPublished => $"{actorName} {actionText} опубликованный судебный акт по делу.",
                UserNotificationTypes.CourtStatusChanged => $"{actorName} {actionText} судебный статус по делу.",
                _ => $"{actorName} {actionText} судебное заседание по делу."
            };

        var builder = new StringBuilder()
            .Append(intro)
            .AppendLine()
            .AppendLine()
            .Append("Дело: ")
            .Append(BuildClientCaseTitle(clientCase))
            .AppendLine()
            .Append("Событие: ")
            .Append(NormalizeNotificationText(courtEvent.Title, "Судебное событие"))
            .AppendLine()
            .Append(string.Equals(action, "rescheduled", StringComparison.OrdinalIgnoreCase) ? "Новая дата: " : "Дата: ")
            .Append(FormatCourtEventDate(courtEvent.ScheduledAt, courtEvent.AllDay, recipientTimeZone))
            .AppendLine()
            .Append("Статус: ")
            .Append(FormatCourtMovementStatus(courtEvent.MovementStatus));

        if (!string.IsNullOrWhiteSpace(courtEvent.CourtName))
        {
            builder.AppendLine().Append("Суд: ").Append(courtEvent.CourtName.Trim());
        }

        if (!string.IsNullOrWhiteSpace(courtEvent.CourtCaseNumber))
        {
            builder.AppendLine().Append("Номер в суде: ").Append(courtEvent.CourtCaseNumber.Trim());
        }

        if (!string.IsNullOrWhiteSpace(courtEvent.ResultSummary))
        {
            builder.AppendLine().Append("Результат: ").Append(courtEvent.ResultSummary.Trim());
        }

        return NotificationTemplateCatalog.CompactBlankLines(builder.ToString());
    }

    private static string BuildClientCaseTitle(ClientCase clientCase)
    {
        var title = NormalizeNotificationText(clientCase.Title, $"Дело #{clientCase.Id.ToString(CultureInfo.InvariantCulture)}");
        return string.IsNullOrWhiteSpace(clientCase.CaseNumber)
            ? title
            : $"№{clientCase.CaseNumber.Trim()} · {title}";
    }

    private static string BuildCaseOwnerName(ClientCase clientCase)
        => clientCase.Client?.Name
            ?? clientCase.LegalEntity?.ShortName
            ?? clientCase.LegalEntity?.FullName
            ?? "Контрагент не указан";

    internal static string ResolveCourtEventNotificationType(ClientCaseCourtEvent courtEvent)
    {
        var isScheduled = string.Equals(courtEvent.MovementStatus, "scheduled", StringComparison.OrdinalIgnoreCase)
            || string.Equals(courtEvent.MovementStatus, "pending", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(courtEvent.EventType, CourtSyncNormalization.EventTypes.Decision, StringComparison.OrdinalIgnoreCase))
        {
            var decisionRecorded = string.Equals(courtEvent.MovementStatus, CourtSyncNormalization.MovementStatuses.Held, StringComparison.OrdinalIgnoreCase)
                || string.Equals(courtEvent.MovementStatus, CourtSyncNormalization.MovementStatuses.Completed, StringComparison.OrdinalIgnoreCase);
            return decisionRecorded ? UserNotificationTypes.CourtDecisionRecorded : UserNotificationTypes.CourtStatusChanged;
        }

        if (!isScheduled || !string.IsNullOrWhiteSpace(courtEvent.ResultSummary))
        {
            return UserNotificationTypes.CourtStatusChanged;
        }

        return UserNotificationTypes.CourtHearing;
    }

    private static string ResolveCourtEventTemplateKey(string notificationType)
        => notificationType switch
        {
            UserNotificationTypes.CourtDecisionRecorded => NotificationTemplateKeys.CourtDecisionRecorded,
            UserNotificationTypes.CourtDecisionPublished => NotificationTemplateKeys.CourtDecisionPublished,
            UserNotificationTypes.CourtStatusChanged => NotificationTemplateKeys.CourtStatusChanged,
            _ => NotificationTemplateKeys.CourtHearing
        };

    private static string ResolveCourtEventTone(string notificationType)
        => notificationType switch
        {
            UserNotificationTypes.CourtDecisionRecorded => "emerald",
            UserNotificationTypes.CourtDecisionPublished => "emerald",
            UserNotificationTypes.CourtStatusChanged => "blue",
            _ => "cyan"
        };

    private static string NormalizeCourtEventAction(string action)
        => string.IsNullOrWhiteSpace(action) ? "updated" : action.Trim();

    private static string ResolveCourtEventActionText(string action)
        => NormalizeCourtEventAction(action).Equals("created", StringComparison.OrdinalIgnoreCase)
            ? "добавил(а)"
            : NormalizeCourtEventAction(action).Equals("rescheduled", StringComparison.OrdinalIgnoreCase)
                ? "перенёс(ла)"
                : "обновил(а)";

    private static string NormalizeCaseProceduralDeadlineAction(string action)
        => string.IsNullOrWhiteSpace(action) ? "updated" : action.Trim();

    private static string ResolveCaseProceduralDeadlineActionText(string action)
        => NormalizeCaseProceduralDeadlineAction(action) switch
        {
            "created" => "добавил(а) процессуальный срок",
            "closed" => "закрыл(а) процессуальный срок",
            "reminder" => "напоминание о процессуальном сроке",
            _ => "обновил(а) процессуальный срок"
        };

    private static string ResolveCaseProceduralDeadlineTone(string action, ClientCaseProceduralDeadline deadline)
        => NormalizeCaseProceduralDeadlineAction(action) switch
        {
            "closed" => "emerald",
            "reminder" when deadline.DueAt.Date < DateTime.UtcNow.Date => "rose",
            "reminder" => "amber",
            "created" => "cyan",
            _ => "blue"
        };

    private static string NormalizeCaseDocumentAction(string action)
        => string.IsNullOrWhiteSpace(action) ? "updated" : action.Trim();

    private static string ResolveCaseDocumentActionText(string action, bool requiresAction)
    {
        if (requiresAction)
        {
            return "изменил(а) документ, который требует действия";
        }

        return NormalizeCaseDocumentAction(action) switch
        {
            "uploaded" => "загрузил(а) документ",
            "version-created" => "загрузил(а) новую версию документа",
            "generated" => "сформировал(а) документ",
            "updated" => "обновил(а) карточку документа",
            "deleted" => "удалил(а) документ",
            _ => "изменил(а) документ"
        };
    }

    private static string ResolveCaseDocumentTone(string action, bool requiresAction)
    {
        if (requiresAction)
        {
            return "amber";
        }

        return NormalizeCaseDocumentAction(action) switch
        {
            "deleted" => "rose",
            "updated" => "blue",
            "version-created" => "cyan",
            _ => "emerald"
        };
    }

    private static bool IsCaseDocumentActionRequired(ClientCaseDocument document)
    {
        var normalizedStatus = NormalizeNotificationText(document.Status, string.Empty).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedStatus))
        {
            return false;
        }

        return string.Equals(normalizedStatus, DocumentWorkflowStatus.NeedsCorrection, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedStatus, DocumentWorkflowStatus.RequiresClientSignature, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedStatus, DocumentWorkflowStatus.ApprovalRejected, StringComparison.OrdinalIgnoreCase)
            || normalizedStatus.Contains("треб", StringComparison.OrdinalIgnoreCase)
            || normalizedStatus.Contains("исправ", StringComparison.OrdinalIgnoreCase)
            || normalizedStatus.Contains("коррект", StringComparison.OrdinalIgnoreCase)
            || normalizedStatus.Contains("подпис", StringComparison.OrdinalIgnoreCase)
            || normalizedStatus.Contains("отклон", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatCourtEventDate(DateTime value, bool allDay)
        => allDay
            ? value.ToString("dd.MM.yyyy", RuCulture)
            : value.ToString("dd.MM.yyyy HH:mm", RuCulture);

    private static string FormatCourtEventDate(DateTime value, bool allDay, TimeZoneInfo recipientTimeZone)
        => allDay
            ? DateTime.SpecifyKind(value.Date, DateTimeKind.Unspecified).ToString("dd.MM.yyyy", RuCulture)
            : CalendarTimeZoneHelper
                .ToUserLocal(value, recipientTimeZone)
                .ToString("dd.MM.yyyy HH:mm", RuCulture);

    private static string FormatFileSize(long sizeBytes)
    {
        if (sizeBytes < 1024)
        {
            return $"{Math.Max(sizeBytes, 0).ToString(CultureInfo.InvariantCulture)} Б";
        }

        if (sizeBytes < 1024 * 1024)
        {
            return $"{(sizeBytes / 1024m).ToString("0.#", RuCulture)} КБ";
        }

        return $"{(sizeBytes / 1024m / 1024m).ToString("0.#", RuCulture)} МБ";
    }

    private static DateTime NormalizeUtcInstant(DateTime value)
        => CalendarTimeZoneHelper.NormalizeUtcInstant(value);

    private static string FormatCaseProceduralDeadlineStatus(string? status)
        => status?.Trim() switch
        {
            "active" => "активен",
            "closed" => "закрыт",
            "cancelled" => "отменен",
            "suspended" => "приостановлен",
            _ => NormalizeNotificationText(status, "не указан")
        };

    private static string FormatCourtMovementStatus(string? status)
        => status?.Trim() switch
        {
            "scheduled" => "назначено",
            "pending" => "ожидается",
            "held" => "проведено",
            "completed" => "завершено",
            "postponed" => "отложено",
            "cancelled" => "отменено",
            "decision" => "решение опубликовано",
            _ when string.IsNullOrWhiteSpace(status) => "не указан",
            _ => status!.Trim()
        };

    private static string FormatCalendarAccessUserList(
        IReadOnlyCollection<int> userIds,
        IReadOnlyDictionary<int, string> namesById)
    {
        var names = userIds
            .Where(userId => userId > 0)
            .Distinct()
            .Select(userId => namesById.TryGetValue(userId, out var name) && !string.IsNullOrWhiteSpace(name)
                ? name.Trim()
                : $"ID {userId}")
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        return names.Length == 0 ? "нет" : string.Join(", ", names);
    }

    private static string NormalizeEventKeySegment(string value)
    {
        var normalized = new string(value
            .Trim()
            .Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-')
            .ToArray());
        normalized = string.Join('-', normalized.Split('-', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(normalized) ? "event" : normalized;
    }

    private static IReadOnlyList<string> FormatTaskChangeLabels(IReadOnlyList<string> changeLabels, TimeZoneInfo timeZone)
        => changeLabels
            .Select(label => FormatTaskDueAtChangeLabel(label, timeZone))
            .ToList();

    private static string FormatTaskDueAtChangeLabel(string label, TimeZoneInfo timeZone)
    {
        const string prefix = "Срок:";
        var normalized = label.Trim();
        if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return label;
        }

        var value = normalized[prefix.Length..].Trim();
        var separator = value.Contains("->", StringComparison.Ordinal)
            ? "->"
            : value.Contains('→', StringComparison.Ordinal) ? "→" : null;
        if (separator is null)
        {
            return label;
        }

        var parts = value.Split(separator, 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || !TryParseHistoryDateTime(parts[0], out var oldDueAt)
            || !TryParseHistoryDateTime(parts[1], out var newDueAt))
        {
            return label;
        }

        return $"Срок: {FormatTaskDueAt(oldDueAt, timeZone)} -> {FormatTaskDueAt(newDueAt, timeZone)}";
    }

    private static bool TryParseHistoryDateTime(string value, out DateTime dateTime)
    {
        if (DateTime.TryParseExact(
                value,
                "dd.MM.yyyy HH:mm",
                RuCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            dateTime = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            return true;
        }

        dateTime = default;
        return false;
    }

    private static string BuildTaskCommentBody(TaskJournalItem task, TaskJournalComment comment, TimeZoneInfo timeZone)
    {
        var dueText = FormatTaskDueAt(task.DueAt, timeZone);
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
            .Where(channel => channel is NotificationChannels.Email
                or NotificationChannels.Telegram
                or NotificationChannels.Sms)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool HasTaskDueAtChange(IReadOnlyList<string> changeLabels)
        => changeLabels.Any(label =>
        {
            var normalized = label.Trim();
            return normalized.Equals("Срок", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("Срок:", StringComparison.OrdinalIgnoreCase);
        });

    private static string ResolveTaskAssignedTone(TaskJournalItem task)
    {
        if (task.DueAt == default)
        {
            return "blue";
        }

        var dueAt = DateTime.SpecifyKind(task.DueAt, DateTimeKind.Utc);
        return dueAt <= DateTime.UtcNow.AddDays(1) ? "rose" : "blue";
    }

    private static string ResolveDueAtChangeTone(TaskJournalItem task)
    {
        var dueAt = DateTime.SpecifyKind(task.DueAt, DateTimeKind.Utc);
        return dueAt <= DateTime.UtcNow.AddDays(1) ? "rose" : "amber";
    }

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
            .Where(userId => userId > 0 && !UserNotificationAntiSpamPolicy.ShouldSuppressSelfNotification(userId, actorUserId))
            .Distinct()
            .ToList();
    }

    private static IReadOnlyList<int> ResolveTaskAssigneeUserIds(TaskJournalItem task)
    {
        var userIds = new List<int>();
        if (task.AssigneeUserId.HasValue)
        {
            userIds.Add(task.AssigneeUserId.Value);
        }

        userIds.AddRange(task.Participants
            .Where(participant =>
                participant.UserId.HasValue
                && string.Equals(participant.Role, "assignee", StringComparison.OrdinalIgnoreCase))
            .Select(participant => participant.UserId!.Value));

        return userIds
            .Where(userId => userId > 0)
            .Distinct()
            .ToList();
    }

    private static IReadOnlyList<int> ResolveCurrentApproverUserIds(TaskJournalItem task, int actorUserId)
    {
        var currentApprover = GetCurrentApprover(task);
        if (currentApprover?.UserId is not { } userId
            || userId <= 0
            || UserNotificationAntiSpamPolicy.ShouldSuppressSelfNotification(userId, actorUserId))
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
            .Where(userId => userId > 0 && !UserNotificationAntiSpamPolicy.ShouldSuppressSelfNotification(userId, actorUserId))
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
            .Where(userId => userId > 0 && !UserNotificationAntiSpamPolicy.ShouldSuppressSelfNotification(userId, actorUserId))
            .Distinct()
            .ToList();
    }

    private static IReadOnlyList<int> ResolveDeadlineChangeRequestRecipientUserIds(
        TaskJournalItem task,
        TaskJournalDeadlineChangeRequest request,
        int actorUserId,
        bool includeReviewer,
        bool includeRequester,
        bool includeObservers)
    {
        var userIds = new List<int>();

        if (includeReviewer)
        {
            if (request.ReviewerUserId.HasValue)
            {
                userIds.Add(request.ReviewerUserId.Value);
            }
            else if (task.AuthorUserId.HasValue)
            {
                userIds.Add(task.AuthorUserId.Value);
            }
        }

        if (includeRequester)
        {
            if (request.RequestedByUserId.HasValue)
            {
                userIds.Add(request.RequestedByUserId.Value);
            }
            else if (task.AssigneeUserId.HasValue)
            {
                userIds.Add(task.AssigneeUserId.Value);
            }
        }

        if (includeObservers)
        {
            userIds.AddRange(task.Participants
                .Where(participant => string.Equals(participant.Role, "observer", StringComparison.OrdinalIgnoreCase))
                .Where(participant => participant.UserId.HasValue)
                .Select(participant => participant.UserId!.Value));
        }

        return userIds
            .Where(userId => userId > 0 && !UserNotificationAntiSpamPolicy.ShouldSuppressSelfNotification(userId, actorUserId))
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

    private static string? ResolveSmsDeliveryError(SmsSendResult result)
    {
        if (string.IsNullOrWhiteSpace(result.Error))
        {
            return null;
        }

        return result.ErrorIsSafe
            ? Truncate(result.Error.Trim(), 500)
            : "SMS не отправлено: провайдер не подтвердил отправку.";
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string NormalizeCommand(string? value)
        => (value ?? string.Empty).Trim().TrimStart('/').ToLowerInvariant();
}
