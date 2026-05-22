using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.LegalIntegrations;
using PayPlanner.Api.Services.LegalIntegrations.Models;
using PayPlanner.Api.Services.Notifications;

namespace PayPlanner.Api.Controllers.V2;

[ApiController]
[Route("api/v2/cases")]
[Authorize]
public class CasesV2Controller : ControllerBase
{
    private const long MaxCaseDocumentBytes = 25 * 1024 * 1024;
    private const int MaxRegistryPageSize = 200;
    private const int MaxRegistryExportRows = 5000;
    private const string RelatedPartySourceClient = "client";
    private const string RelatedPartySourceExternalContact = "externalContact";
    private const string RelatedPartySourceLegalEntity = "legalEntity";
    private const string CaseConversationPrefix = "Внутреннее сообщение:";
    private const string CaseHistoryCommentPrefix = "Комментарий по делу:";
    private const string CaseHistoryAuthorMarker = "Автор:";
    private const string CaseDocumentTemplatesSettingKey = "cases.documentTemplates";
    private static readonly JsonSerializerOptions CaseDocumentTemplateJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions UserNotificationJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> AllowedCasePriorities = new(StringComparer.OrdinalIgnoreCase)
    {
        "low",
        "normal",
        "high",
        "urgent",
    };
    private static readonly CaseDocumentTemplateDefinition[] CaseDocumentTemplateDefinitions =
    [
        new("case-summary", "Справка по делу", "Карточка дела", "Справка по делу"),
        new("claim", "Исковое заявление", "Иск", "Исковое заявление"),
        new("motion", "Ходатайство", "Ходатайство", "Ходатайство"),
        new("objection", "Возражение", "Возражение", "Возражение"),
        new("court-letter", "Письмо в суд", "Переписка", "Письмо в суд"),
        new("power-of-attorney", "Доверенность", "Доверенность", "Доверенность"),
        new("application", "Заявление", "Заявление", "Заявление"),
        new("procedural-document", "Процессуальный документ", "Процессуальный документ", "Процессуальный документ"),
    ];
    private static readonly CaseDocumentTemplateTokenDefinition[] CaseDocumentTemplateTokens =
    [
        new("case.id", "Дело", "Внутренний ID дела."),
        new("case.title", "Дело", "Название дела."),
        new("case.number", "Дело", "Номер дела в CRM."),
        new("case.type", "Дело", "Тип дела."),
        new("case.stage", "Дело", "Стадия дела."),
        new("case.priority", "Дело", "Приоритет дела."),
        new("client.name", "Клиент", "Клиент, доверитель или связанное юридическое лицо дела."),
        new("responsible.name", "Команда", "Ответственный юрист или сотрудник."),
        new("court.name", "Суд", "Наименование суда."),
        new("court.caseNumber", "Суд", "Номер судебного дела."),
        new("claimant", "Участники", "Истец или заявитель."),
        new("defendant", "Участники", "Ответчик или должник."),
        new("thirdParties", "Участники", "Третьи лица."),
        new("representatives", "Участники", "Представители по делу."),
        new("judge.name", "Суд", "Судья."),
        new("nextHearingAt", "Сроки", "Дата и время ближайшего заседания."),
        new("claimFiledAt", "Сроки", "Дата подачи заявления."),
        new("courtDecisionAt", "Сроки", "Дата судебного решения."),
        new("appealDeadlineAt", "Сроки", "Срок апелляции."),
        new("cassationDeadlineAt", "Сроки", "Срок кассации."),
        new("document.type", "Документ", "Тип формируемого документа."),
        new("today", "Системные", "Текущая дата в формате ДД.ММ.ГГГГ."),
        new("now", "Системные", "Текущая дата и время в формате ДД.ММ.ГГГГ ЧЧ:ММ."),
    ];
    private static readonly string[] DefaultCaseDocumentTypes =
    [
        "Иск",
        "Ходатайство",
        "Жалоба",
        "Возражение",
        "Судебный акт",
        "Доверенность",
        "Переписка",
        "Доказательство",
        "Договорной/финансовый документ",
        "Внутренний служебный файл",
    ];

    private readonly PaymentContext _db;
    private readonly ICaseDocumentStorage _caseDocumentStorage;
    private readonly ISudrfCourtSyncService? _sudrfCourtSyncService;

    public CasesV2Controller(
        PaymentContext db,
        IWebHostEnvironment? environment = null,
        ICaseDocumentStorage? caseDocumentStorage = null,
        ISudrfCourtSyncService? sudrfCourtSyncService = null)
    {
        _db = db;
        _caseDocumentStorage = caseDocumentStorage ?? new LocalCaseDocumentStorage(environment);
        _sudrfCourtSyncService = sudrfCourtSyncService;
    }

    private Task<DataVisibilityAccess?> ResolveCasesVisibilityAsync(CancellationToken ct)
        => _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Cases, ct);

    private async Task<ActionResult?> RequireCasesPermissionAsync(
        Func<RolePermission, bool> predicate,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Forbid();
        }

        var isAdmin = User.IsInRole("admin") || await _db.IsUserAdminAsync(userId.Value, ct);
        if (isAdmin)
        {
            return null;
        }

        var permission = await _db.GetUserSectionPermissionAsync(userId.Value, RolePermissionCatalog.Cases, ct);
        return permission is not null && predicate(permission) ? null : Forbid();
    }

    private async Task<ActionResult?> RequireDocumentTemplateEditPermissionAsync(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Forbid();
        }

        var isAdmin = User.IsInRole("admin") || await _db.IsUserAdminAsync(userId.Value, ct);
        if (isAdmin)
        {
            return null;
        }

        var permission = await _db.GetUserSectionPermissionAsync(userId.Value, RolePermissionCatalog.Dictionaries, ct);
        return permission is not null && permission.CanEdit ? null : Forbid();
    }

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] int? clientId,
        [FromQuery] int? legalEntityId,
        [FromQuery] ClientCaseStatus? status,
        [FromQuery] string? search,
        [FromQuery] int? responsibleUserId,
        [FromQuery] string? caseType,
        [FromQuery] string? priority,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] decimal? minFee,
        [FromQuery] decimal? maxFee,
        [FromQuery] string? sortBy,
        [FromQuery] string? sortDir,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? scope = null,
        [FromQuery] string? activity = null,
        [FromQuery] int? inactiveDays = null,
        CancellationToken ct = default)
    {
        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxRegistryPageSize);

        var q = BuildRegistryQuery(
            visibility,
            clientId,
            legalEntityId,
            status,
            search,
            responsibleUserId,
            caseType,
            priority,
            from,
            to,
            minFee,
            maxFee,
            sortBy,
            sortDir,
            scope,
            activity,
            inactiveDays);

        var total = await q.CountAsync(ct);
        var items = await q
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .AsNoTracking()
            .ToListAsync(ct);
        await ApplyFinanceSummaryAsync(items, ct);
        await ApplyTaskSummaryAsync(items, ct);

        return Ok(new { items, total, page, pageSize });
    }

    [HttpGet("export.csv")]
    public async Task<IActionResult> ExportRegistryCsv(
        [FromQuery] int? clientId,
        [FromQuery] int? legalEntityId,
        [FromQuery] ClientCaseStatus? status,
        [FromQuery] string? search,
        [FromQuery] int? responsibleUserId,
        [FromQuery] string? caseType,
        [FromQuery] string? priority,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] decimal? minFee,
        [FromQuery] decimal? maxFee,
        [FromQuery] string? sortBy,
        [FromQuery] string? sortDir,
        [FromQuery] string? scope = null,
        [FromQuery] string? activity = null,
        [FromQuery] int? inactiveDays = null,
        CancellationToken ct = default)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanExport, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var items = await BuildRegistryQuery(
                visibility,
                clientId,
                legalEntityId,
                status,
                search,
                responsibleUserId,
                caseType,
                priority,
                from,
                to,
                minFee,
                maxFee,
                sortBy,
                sortDir,
                scope,
                activity,
                inactiveDays)
            .Take(MaxRegistryExportRows)
            .AsNoTracking()
            .ToListAsync(ct);
        await ApplyFinanceSummaryAsync(items, ct);
        await ApplyTaskSummaryAsync(items, ct);

        var bytes = BuildRegistryCsv(items);
        var fileName = $"cases-registry-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv";
        return File(bytes, "text/csv; charset=utf-8", fileName);
    }

    [HttpGet("print")]
    public async Task<IActionResult> PrintRegistry(
        [FromQuery] int? clientId,
        [FromQuery] int? legalEntityId,
        [FromQuery] ClientCaseStatus? status,
        [FromQuery] string? search,
        [FromQuery] int? responsibleUserId,
        [FromQuery] string? caseType,
        [FromQuery] string? priority,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] decimal? minFee,
        [FromQuery] decimal? maxFee,
        [FromQuery] string? sortBy,
        [FromQuery] string? sortDir,
        [FromQuery] string? scope = null,
        [FromQuery] string? activity = null,
        [FromQuery] int? inactiveDays = null,
        CancellationToken ct = default)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanExport, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var items = await BuildRegistryQuery(
                visibility,
                clientId,
                legalEntityId,
                status,
                search,
                responsibleUserId,
                caseType,
                priority,
                from,
                to,
                minFee,
                maxFee,
                sortBy,
                sortDir,
                scope,
                activity,
                inactiveDays)
            .Take(MaxRegistryExportRows)
            .AsNoTracking()
            .ToListAsync(ct);
        await ApplyFinanceSummaryAsync(items, ct);
        await ApplyTaskSummaryAsync(items, ct);

        return Content(BuildRegistryPrintHtml(items), "text/html; charset=utf-8", Encoding.UTF8);
    }

    private IQueryable<ClientCase> BuildRegistryQuery(
        DataVisibilityAccess visibility,
        int? clientId,
        int? legalEntityId,
        ClientCaseStatus? status,
        string? search,
        int? responsibleUserId,
        string? caseType,
        string? priority,
        DateTime? from,
        DateTime? to,
        decimal? minFee,
        decimal? maxFee,
        string? sortBy,
        string? sortDir,
        string? scope,
        string? activity,
        int? inactiveDays)
        => _db.ClientCases.AsQueryable()
            .WithCaseIncludes()
            .ApplyClientCaseVisibility(visibility)
            .ApplyCaseRegistryScope(scope, visibility.UserId)
            .ApplyCaseFilters(clientId, legalEntityId, status, search, responsibleUserId, caseType, priority, from, to, minFee, maxFee)
            .ApplyCaseActivityFilter(activity, inactiveDays, _db.TaskJournalItems.IgnoreQueryFilters())
            .ApplyCaseSort(sortBy, sortDir);

    private static IQueryable<ClientCase> ApplyClientCaseAccessManagementVisibility(
        IQueryable<ClientCase> query,
        DataVisibilityAccess visibility)
    {
        if (visibility.SeesAll)
        {
            return query;
        }

        var visibleUserIds = visibility.VisibleUserIds.ToArray();
        return query.Where(clientCase =>
            (clientCase.ResponsibleUserId.HasValue
                && visibleUserIds.Contains(clientCase.ResponsibleUserId.Value))
            || (clientCase.Client != null
                && clientCase.Client.ResponsibleUserId.HasValue
                && visibleUserIds.Contains(clientCase.Client.ResponsibleUserId.Value))
            || (clientCase.LegalEntity != null
                && clientCase.LegalEntity.ResponsibleUserId.HasValue
                && visibleUserIds.Contains(clientCase.LegalEntity.ResponsibleUserId.Value)));
    }

    private async Task<IReadOnlyList<int>> ResolveCaseAccessManagerUserIdsAsync(
        ClientCase clientCase,
        int requesterUserId,
        CancellationToken ct)
    {
        var targetUserIds = new HashSet<int>();
        AddIfPositive(targetUserIds, clientCase.ResponsibleUserId);
        AddIfPositive(targetUserIds, clientCase.Client?.ResponsibleUserId);
        AddIfPositive(targetUserIds, clientCase.LegalEntity?.ResponsibleUserId);

        var targetUsers = await _db.Users
            .AsNoTracking()
            .Where(item => targetUserIds.Contains(item.Id))
            .Select(item => new
            {
                item.Id,
                item.DepartmentId,
                item.Department,
            })
            .ToListAsync(ct);

        var candidates = await _db.Users
            .AsNoTracking()
            .Where(item => item.IsActive && item.IsApproved)
            .Select(item => new
            {
                item.Id,
                item.DepartmentId,
                item.Department,
                RoleName = item.Role != null ? item.Role.Name : string.Empty,
                Permission = item.Role == null
                    ? null
                    : item.Role.Permissions
                        .Where(permission => permission.Section == RolePermissionCatalog.Cases)
                        .Select(permission => new
                        {
                            permission.CanView,
                            permission.CanEdit,
                            permission.ViewScope,
                        })
                        .FirstOrDefault(),
            })
            .ToListAsync(ct);

        return candidates
            .Where(candidate => candidate.Id != requesterUserId)
            .Where(candidate =>
            {
                var isAdmin = string.Equals(candidate.RoleName, "admin", StringComparison.OrdinalIgnoreCase);
                if (!isAdmin && candidate.Permission is not { CanView: true, CanEdit: true })
                {
                    return false;
                }

                var scope = isAdmin
                    ? DataVisibilityScope.All
                    : DataVisibilityScope.Normalize(candidate.Permission?.ViewScope, DataVisibilityScope.Own);

                if (string.Equals(scope, DataVisibilityScope.All, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (string.Equals(scope, DataVisibilityScope.Own, StringComparison.OrdinalIgnoreCase))
                {
                    return targetUserIds.Contains(candidate.Id);
                }

                if (!string.Equals(scope, DataVisibilityScope.Department, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var candidateDepartment = candidate.Department?.Trim();
                return targetUsers.Any(target =>
                    (candidate.DepartmentId.HasValue
                        && target.DepartmentId.HasValue
                        && candidate.DepartmentId.Value == target.DepartmentId.Value)
                    || (!string.IsNullOrWhiteSpace(candidateDepartment)
                        && !string.IsNullOrWhiteSpace(target.Department)
                        && string.Equals(candidateDepartment, target.Department.Trim(), StringComparison.Ordinal)));
            })
            .Select(candidate => candidate.Id)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
    }

    private async Task AddCaseUserNotificationAsync(
        int recipientUserId,
        string type,
        string eventKey,
        string title,
        string body,
        string tone,
        ClientCase clientCase,
        object? metadata,
        CancellationToken ct)
    {
        if (recipientUserId <= 0 || string.IsNullOrWhiteSpace(eventKey))
        {
            return;
        }

        if (!await IsCaseUserNotificationTypeEnabledAsync(recipientUserId, type, ct))
        {
            return;
        }

        var alreadyExists = await _db.UserNotifications
            .AsNoTracking()
            .AnyAsync(item => item.RecipientUserId == recipientUserId && item.EventKey == eventKey, ct);
        if (alreadyExists)
        {
            return;
        }

        var recipientExists = await _db.Users
            .AsNoTracking()
            .AnyAsync(item => item.Id == recipientUserId && item.IsActive, ct);
        if (!recipientExists)
        {
            return;
        }

        _db.UserNotifications.Add(new UserNotification
        {
            RecipientUserId = recipientUserId,
            Type = type,
            EventKey = eventKey,
            Title = Truncate(title.Trim(), 300),
            BodyPreview = Truncate(body.Trim(), 1000),
            Tone = string.IsNullOrWhiteSpace(tone) ? "blue" : tone.Trim(),
            ObjectType = "case",
            ObjectId = clientCase.Id,
            MetadataJson = metadata is null ? "{}" : JsonSerializer.Serialize(metadata, UserNotificationJsonOptions),
            CreatedAt = DateTime.UtcNow,
        });
    }

    private async Task<bool> IsCaseUserNotificationTypeEnabledAsync(int recipientUserId, string type, CancellationToken ct)
    {
        var setting = await _db.UserSettings
            .AsNoTracking()
            .Where(item =>
                item.UserId == recipientUserId
                && item.Key == UserNotificationPreferenceSettings.SettingKey)
            .Select(item => item.Value)
            .FirstOrDefaultAsync(ct);

        return UserNotificationPreferenceSettings.IsTypeEnabled(setting, type);
    }

    private static void AddIfPositive(ISet<int> values, int? value)
    {
        if (value.HasValue && value.Value > 0)
        {
            values.Add(value.Value);
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

    private async Task AddCourtEventUserNotificationsAsync(
        ClientCase clientCase,
        ClientCaseCourtEvent courtEvent,
        string action,
        int? actorUserId,
        string actorName,
        CancellationToken ct)
    {
        var recipientUserIds = new HashSet<int>();
        AddIfPositive(recipientUserIds, clientCase.ResponsibleUserId);
        AddIfPositive(recipientUserIds, clientCase.Client?.ResponsibleUserId);
        AddIfPositive(recipientUserIds, clientCase.LegalEntity?.ResponsibleUserId);

        if (recipientUserIds.Count == 0)
        {
            return;
        }

        var isCreated = string.Equals(action, "created", StringComparison.OrdinalIgnoreCase);
        var title = isCreated
            ? $"Новое судебное событие: {BuildCaseNotificationTitle(clientCase)}"
            : $"Обновлено судебное событие: {BuildCaseNotificationTitle(clientCase)}";
        var body = BuildCourtEventNotificationBody(clientCase, courtEvent, isCreated, actorName);
        var eventVersionTicks = (courtEvent.UpdatedAt ?? courtEvent.CreatedAt).Ticks;
        var eventKey = $"case:{clientCase.Id}:court-event:{courtEvent.Id}:{action}:{eventVersionTicks}";
        var metadata = new
        {
            clientCaseId = clientCase.Id,
            courtEventId = courtEvent.Id,
            action,
            actorUserId,
            eventType = courtEvent.EventType,
            movementStatus = courtEvent.MovementStatus,
            scheduledAt = courtEvent.ScheduledAt,
            courtName = courtEvent.CourtName,
            courtCaseNumber = courtEvent.CourtCaseNumber,
            clientCaseDocumentId = courtEvent.ClientCaseDocumentId,
            proceduralDeadlineId = courtEvent.ProceduralDeadlineId,
        };

        foreach (var recipientUserId in recipientUserIds.OrderBy(static id => id))
        {
            await AddCaseUserNotificationAsync(
                recipientUserId,
                UserNotificationTypes.CaseCourtEvent,
                eventKey,
                title,
                body,
                "cyan",
                clientCase,
                metadata,
                ct);
        }
    }

    private static string BuildCourtEventNotificationBody(
        ClientCase clientCase,
        ClientCaseCourtEvent courtEvent,
        bool isCreated,
        string actorName)
    {
        var builder = new StringBuilder()
            .Append(isCreated ? "Добавлено судебное событие." : "Обновлено судебное событие.")
            .AppendLine()
            .Append("Дело: ")
            .Append(BuildCaseNotificationTitle(clientCase))
            .AppendLine()
            .Append("Событие: ")
            .Append(courtEvent.Title)
            .AppendLine()
            .Append("Дата: ")
            .Append(courtEvent.ScheduledAt.ToString(courtEvent.AllDay ? "dd.MM.yyyy" : "dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture))
            .AppendLine()
            .Append("Автор: ")
            .Append(actorName);

        if (!string.IsNullOrWhiteSpace(courtEvent.CourtName))
        {
            builder
                .AppendLine()
                .Append("Суд: ")
                .Append(courtEvent.CourtName);
        }

        if (!string.IsNullOrWhiteSpace(courtEvent.CourtCaseNumber))
        {
            builder
                .AppendLine()
                .Append("Номер в суде: ")
                .Append(courtEvent.CourtCaseNumber);
        }

        if (!string.IsNullOrWhiteSpace(courtEvent.MovementStatus))
        {
            builder
                .AppendLine()
                .Append("Статус: ")
                .Append(courtEvent.MovementStatus);
        }

        return builder.ToString();
    }

    private async Task AddProceduralDeadlineUserNotificationsAsync(
        ClientCase clientCase,
        ClientCaseProceduralDeadline deadline,
        string action,
        int? actorUserId,
        string actorName,
        CancellationToken ct)
    {
        var recipientUserIds = new HashSet<int>();
        AddIfPositive(recipientUserIds, clientCase.ResponsibleUserId);
        AddIfPositive(recipientUserIds, clientCase.Client?.ResponsibleUserId);
        AddIfPositive(recipientUserIds, clientCase.LegalEntity?.ResponsibleUserId);

        if (recipientUserIds.Count == 0)
        {
            return;
        }

        var title = action switch
        {
            "created" => $"Новый процессуальный срок: {BuildCaseNotificationTitle(clientCase)}",
            "closed" => $"Закрыт процессуальный срок: {BuildCaseNotificationTitle(clientCase)}",
            _ => $"Обновлен процессуальный срок: {BuildCaseNotificationTitle(clientCase)}",
        };
        var body = BuildProceduralDeadlineNotificationBody(clientCase, deadline, action, actorName);
        var eventVersionTicks = (deadline.UpdatedAt ?? deadline.CreatedAt).Ticks;
        var eventKey = $"case:{clientCase.Id}:procedural-deadline:{deadline.Id}:{action}:{eventVersionTicks}";
        var metadata = new
        {
            clientCaseId = clientCase.Id,
            proceduralDeadlineId = deadline.Id,
            action,
            actorUserId,
            deadlineType = deadline.DeadlineType,
            dueAt = deadline.DueAt,
            status = deadline.Status,
            warningDaysBefore = deadline.WarningDaysBefore,
            taskJournalItemId = deadline.TaskJournalItemId,
            clientCaseDocumentId = deadline.ClientCaseDocumentId,
            sourceType = deadline.SourceType,
            sourceId = deadline.SourceId,
        };

        foreach (var recipientUserId in recipientUserIds.OrderBy(static id => id))
        {
            await AddCaseUserNotificationAsync(
                recipientUserId,
                UserNotificationTypes.CaseProceduralDeadline,
                eventKey,
                title,
                body,
                "amber",
                clientCase,
                metadata,
                ct);
        }
    }

    private static string BuildProceduralDeadlineNotificationBody(
        ClientCase clientCase,
        ClientCaseProceduralDeadline deadline,
        string action,
        string actorName)
    {
        var actionText = action switch
        {
            "created" => "Добавлен процессуальный срок.",
            "closed" => "Процессуальный срок закрыт.",
            _ => "Процессуальный срок обновлен.",
        };
        var builder = new StringBuilder()
            .Append(actionText)
            .AppendLine()
            .Append("Дело: ")
            .Append(BuildCaseNotificationTitle(clientCase))
            .AppendLine()
            .Append("Срок: ")
            .Append(deadline.Title)
            .AppendLine()
            .Append("Дата: ")
            .Append(deadline.DueAt.ToString(deadline.AllDay ? "dd.MM.yyyy" : "dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture))
            .AppendLine()
            .Append("Статус: ")
            .Append(deadline.Status)
            .AppendLine()
            .Append("Автор: ")
            .Append(actorName);

        if (!string.IsNullOrWhiteSpace(deadline.CloseReason))
        {
            builder
                .AppendLine()
                .Append("Причина закрытия: ")
                .Append(deadline.CloseReason);
        }

        if (!string.IsNullOrWhiteSpace(deadline.SourceType) && deadline.SourceId.HasValue)
        {
            builder
                .AppendLine()
                .Append("Источник: ")
                .Append(deadline.SourceType)
                .Append(" #")
                .Append(deadline.SourceId.Value);
        }

        return builder.ToString();
    }

    private static string BuildCaseOwnerName(ClientCase clientCase)
        => clientCase.Client?.Name
            ?? clientCase.LegalEntity?.ShortName
            ?? clientCase.LegalEntity?.FullName
            ?? "Контрагент не указан";

    private static string BuildCaseAccessRequestNotificationBody(
        ClientCase clientCase,
        string actorName,
        string? reason)
    {
        var builder = new StringBuilder()
            .Append("Пользователь ")
            .Append(actorName)
            .Append(" запросил доступ к скрытому делу.")
            .AppendLine()
            .Append("Контрагент: ")
            .Append(BuildCaseOwnerName(clientCase))
            .AppendLine()
            .Append("Дело: ")
            .Append(BuildCaseNotificationTitle(clientCase));

        if (!string.IsNullOrWhiteSpace(reason))
        {
            builder
                .AppendLine()
                .Append("Причина: ")
                .Append(reason);
        }

        return builder.ToString();
    }

    private static string BuildCaseAccessResolutionNotificationBody(
        ClientCase clientCase,
        bool granted,
        string actorName,
        string? comment)
    {
        var builder = new StringBuilder()
            .Append(actorName)
            .Append(granted ? " разрешил доступ к делу." : " отклонил запрос доступа к делу.")
            .AppendLine()
            .Append("Контрагент: ")
            .Append(BuildCaseOwnerName(clientCase))
            .AppendLine()
            .Append("Дело: ")
            .Append(BuildCaseNotificationTitle(clientCase));

        if (!string.IsNullOrWhiteSpace(comment))
        {
            builder
                .AppendLine()
                .Append("Комментарий: ")
                .Append(comment);
        }

        return builder.ToString();
    }

    private static byte[] BuildRegistryCsv(IReadOnlyCollection<ClientCase> items)
    {
        var header = new[]
        {
            "№ дела",
            "Название",
            "Клиент/юр. лицо",
            "Ответственный",
            "Статус",
            "Тип",
            "Этап",
            "Создано",
            "Начало",
            "Срок",
            "Открытые задачи",
            "Просроченные задачи",
            "Выполненные задачи",
            "Прогресс",
            "Последняя активность",
            "Гонорар",
            "Счета",
            "Оплачено",
            "Долг",
            "Последний просмотр",
            "Активность подтвердил",
            "Приоритет",
            "Суд",
            "№ в суде",
            "Заседание",
        };

        var lines = new List<string>
        {
            string.Join(';', header.Select(CsvCell)),
        };

        foreach (var item in items)
        {
            lines.Add(string.Join(';', new[]
            {
                item.CaseNumber,
                item.Title,
                GetOwnerName(item),
                GetResponsibleName(item),
                GetCaseStatusLabel(item.Status),
                item.CaseType,
                item.Stage,
                FormatRuDate(item.CreatedAt),
                FormatRuDate(item.StartDate),
                FormatRuDate(item.DueDate),
                item.OpenTaskCount.ToString(CultureInfo.InvariantCulture),
                item.OverdueTaskCount.ToString(CultureInfo.InvariantCulture),
                item.CompletedTaskCount.ToString(CultureInfo.InvariantCulture),
                $"{item.TaskProgressPercent}%",
                FormatRuDateTime(GetLatestCaseActivityAt(item)),
                FormatMoney(item.FeeAmount),
                FormatMoney(item.IncomeTotal),
                FormatMoney(item.IncomePaid),
                FormatMoney(item.IncomeOutstanding),
                FormatRuDateTime(item.LastViewedAt),
                item.ActivityConfirmedByUserName,
                GetPriorityLabel(item.Priority),
                item.CourtName,
                item.CourtCaseNumber,
                FormatRuDateTime(item.NextHearingAt),
            }.Select(CsvCell)));
        }

        var csv = string.Join('\n', lines);
        var preamble = Encoding.UTF8.GetPreamble();
        var body = Encoding.UTF8.GetBytes(csv);
        var result = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, result, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, result, preamble.Length, body.Length);
        return result;
    }

    private static string BuildRegistryPrintHtml(IReadOnlyCollection<ClientCase> items)
    {
        var generatedAt = DateTime.Now.ToString("dd.MM.yyyy HH:mm", CultureInfo.GetCultureInfo("ru-RU"));
        var totalFee = items.Sum(item => item.FeeAmount);
        var totalIncome = items.Sum(item => item.IncomeTotal);
        var totalPaid = items.Sum(item => item.IncomePaid);
        var totalDebt = items.Sum(item => item.IncomeOutstanding);
        var rows = string.Join('\n', items.Select(item => $"""
            <tr>
              <td>{Html(item.CaseNumber)}</td>
              <td>
                <strong>{Html(item.Title)}</strong>
                <div class="muted">{Html(GetOwnerName(item))}</div>
              </td>
              <td>{Html(GetResponsibleName(item))}</td>
              <td>{Html(GetCaseStatusLabel(item.Status))}</td>
              <td>{Html(item.CaseType)}</td>
              <td>{Html(item.Stage)}</td>
              <td>{Html(FormatRuDate(item.DueDate))}</td>
              <td class="num">{Html(FormatMoney(item.FeeAmount))}</td>
              <td class="num">{Html(FormatMoney(item.IncomeTotal))}</td>
              <td class="num">{Html(FormatMoney(item.IncomePaid))}</td>
              <td class="num">{Html(FormatMoney(item.IncomeOutstanding))}</td>
              <td>{item.OpenTaskCount} / {item.OverdueTaskCount}</td>
              <td>{Html(FormatRuDateTime(GetLatestCaseActivityAt(item)))}</td>
            </tr>
            """));

        return $$"""
            <!doctype html>
            <html lang="ru">
            <head>
              <meta charset="utf-8">
              <title>Реестр дел</title>
              <style>
                body { font-family: Arial, sans-serif; color: #0f172a; margin: 24px; }
                h1 { margin: 0 0 6px; font-size: 24px; }
                .meta { color: #64748b; margin-bottom: 18px; font-size: 12px; }
                .summary { display: grid; grid-template-columns: repeat(5, minmax(120px, 1fr)); gap: 8px; margin-bottom: 18px; }
                .summary div { border: 1px solid #e2e8f0; border-radius: 8px; padding: 10px; }
                .summary span { display: block; color: #64748b; font-size: 11px; text-transform: uppercase; }
                .summary strong { font-size: 15px; }
                table { border-collapse: collapse; width: 100%; font-size: 11px; }
                th, td { border: 1px solid #cbd5e1; padding: 6px 7px; vertical-align: top; }
                th { background: #f1f5f9; text-align: left; }
                .muted { color: #64748b; margin-top: 3px; }
                .num { white-space: nowrap; text-align: right; }
                @media print {
                  body { margin: 10mm; }
                  .summary { grid-template-columns: repeat(5, 1fr); }
                }
              </style>
            </head>
            <body>
              <h1>Реестр дел</h1>
              <div class="meta">Сформировано: {{Html(generatedAt)}}. В печатную форму попали первые {{MaxRegistryExportRows}} строк текущей выборки.</div>
              <section class="summary">
                <div><span>Дел</span><strong>{{items.Count}}</strong></div>
                <div><span>Гонорар</span><strong>{{Html(FormatMoney(totalFee))}}</strong></div>
                <div><span>Счета</span><strong>{{Html(FormatMoney(totalIncome))}}</strong></div>
                <div><span>Оплачено</span><strong>{{Html(FormatMoney(totalPaid))}}</strong></div>
                <div><span>Долг</span><strong>{{Html(FormatMoney(totalDebt))}}</strong></div>
              </section>
              <table>
                <thead>
                  <tr>
                    <th>№ дела</th>
                    <th>Название / клиент</th>
                    <th>Ответственный</th>
                    <th>Статус</th>
                    <th>Тип</th>
                    <th>Этап</th>
                    <th>Срок</th>
                    <th>Гонорар</th>
                    <th>Счета</th>
                    <th>Оплачено</th>
                    <th>Долг</th>
                    <th>Задачи</th>
                    <th>Активность</th>
                  </tr>
                </thead>
                <tbody>
                  {{rows}}
                </tbody>
              </table>
            </body>
            </html>
            """;
    }

    private static string CsvCell(string? value)
        => $"\"{(value ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string Html(string? value)
        => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string GetOwnerName(ClientCase clientCase)
        => FirstNonEmpty(clientCase.Client?.Name, clientCase.LegalEntity?.ShortName, clientCase.LegalEntity?.FullName, "—");

    private static string GetResponsibleName(ClientCase clientCase)
        => FirstNonEmpty(clientCase.ResponsibleUser?.FullName, "Ответственный владельца");

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string FormatRuDate(DateTime? value)
        => value.HasValue
            ? value.Value.ToString("dd.MM.yyyy", CultureInfo.GetCultureInfo("ru-RU"))
            : string.Empty;

    private static string FormatRuDateTime(DateTime? value)
        => value.HasValue
            ? value.Value.ToString("dd.MM.yyyy HH:mm", CultureInfo.GetCultureInfo("ru-RU"))
            : string.Empty;

    private static string FormatMoney(decimal value)
        => value.ToString("N2", CultureInfo.GetCultureInfo("ru-RU")) + " ₽";

    private static DateTime? GetLatestCaseActivityAt(ClientCase clientCase)
    {
        var values = new[]
        {
            clientCase.LastTaskAt,
            clientCase.ActivityConfirmedAt,
            clientCase.LastViewedAt,
        }.Where(value => value.HasValue).Select(value => value!.Value).ToArray();

        return values.Length == 0 ? null : values.Max();
    }

    private static string GetCaseStatusLabel(ClientCaseStatus status)
        => status switch
        {
            ClientCaseStatus.Open => "В работе",
            ClientCaseStatus.OnHold => "Приостановлено",
            ClientCaseStatus.Closed => "Закрыто",
            ClientCaseStatus.Lost => "Проиграно",
            _ => status.ToString(),
        };

    private static string GetPriorityLabel(string? priority)
        => priority?.Trim().ToLowerInvariant() switch
        {
            "low" => "Низкий",
            "normal" => "Обычный",
            "high" => "Высокий",
            "urgent" => "Критический",
            _ => priority ?? string.Empty,
        };

    [HttpGet("document-templates")]
    [HttpGet("/api/document-templates")]
    public async Task<ActionResult<IReadOnlyList<CaseDocumentTemplateDefinitionDto>>> GetDocumentTemplates(CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var templates = (await GetCaseDocumentTemplateLibraryAsync(ct))
            .Select(MapDocumentTemplateDefinition)
            .ToList();

        return Ok(templates);
    }

    [HttpGet("document-template-tokens")]
    [HttpGet("/api/document-template-tokens")]
    public async Task<ActionResult<IReadOnlyList<CaseDocumentTemplateTokenDto>>> GetDocumentTemplateTokens(CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        return Ok(CaseDocumentTemplateTokens
            .Select(static token => new CaseDocumentTemplateTokenDto
            {
                Key = token.Key,
                Category = token.Category,
                Description = token.Description,
                Placeholder = "{{" + token.Key + "}}",
            })
            .ToList());
    }

    [HttpPost("document-templates")]
    [HttpPost("/api/document-templates")]
    public async Task<ActionResult<CaseDocumentTemplateDefinitionDto>> CreateDocumentTemplate(
        [FromBody] UpsertCaseDocumentTemplateRequest? request,
        CancellationToken ct)
    {
        if (await RequireDocumentTemplateEditPermissionAsync(ct) is { } forbidden)
        {
            return forbidden;
        }

        var validation = BuildCaseDocumentTemplateDefinition(request, existing: null);
        if (validation.Error is not null)
        {
            return BadRequest(new { message = validation.Error });
        }

        var templates = (await GetCaseDocumentTemplateLibraryAsync(ct)).ToList();
        if (templates.Any(item => string.Equals(item.Key, validation.Template.Key, StringComparison.OrdinalIgnoreCase)))
        {
            return Conflict(new { message = "Шаблон с таким кодом уже существует." });
        }

        templates.Add(validation.Template);
        await SaveCaseDocumentTemplateLibraryAsync(templates, ct);

        return Created($"/api/document-templates/{validation.Template.Key}", MapDocumentTemplateDefinition(validation.Template));
    }

    [HttpPatch("document-templates/{templateKey}")]
    [HttpPatch("/api/document-templates/{templateKey}")]
    public async Task<ActionResult<CaseDocumentTemplateDefinitionDto>> UpdateDocumentTemplate(
        string templateKey,
        [FromBody] UpsertCaseDocumentTemplateRequest? request,
        CancellationToken ct)
    {
        if (await RequireDocumentTemplateEditPermissionAsync(ct) is { } forbidden)
        {
            return forbidden;
        }

        var normalizedKey = NormalizeDocumentTemplateCode(templateKey);
        if (normalizedKey is null)
        {
            return BadRequest(new { message = "Некорректный код шаблона." });
        }

        var templates = (await GetCaseDocumentTemplateLibraryAsync(ct)).ToList();
        var index = templates.FindIndex(item => string.Equals(item.Key, normalizedKey, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return NotFound();
        }

        var validation = BuildCaseDocumentTemplateDefinition(request, templates[index] with { Key = normalizedKey });
        if (validation.Error is not null)
        {
            return BadRequest(new { message = validation.Error });
        }

        templates[index] = validation.Template;
        await SaveCaseDocumentTemplateLibraryAsync(templates, ct);

        return Ok(MapDocumentTemplateDefinition(validation.Template));
    }

    [HttpPost("document-templates/{templateKey}/render")]
    [HttpPost("/api/document-templates/{templateKey}/render")]
    public async Task<ActionResult<RenderedCaseDocumentTemplateDto>> RenderDocumentTemplate(
        string templateKey,
        [FromBody] RenderCaseDocumentTemplateRequest? request,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        if (request is null || request.ClientCaseId <= 0)
        {
            return BadRequest(new { message = "Укажите дело для автозаполнения шаблона." });
        }

        var normalizedKey = NormalizeDocumentTemplateCode(templateKey);
        if (normalizedKey is null)
        {
            return BadRequest(new { message = "Некорректный код шаблона." });
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var clientCase = await _db.ClientCases
            .WithCaseIncludes()
            .ApplyClientCaseVisibility(visibility)
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == request.ClientCaseId, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var templates = await GetCaseDocumentTemplateLibraryAsync(ct);
        var template = templates.FirstOrDefault(item =>
            item.IsActive && string.Equals(item.Key, normalizedKey, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(template.Key))
        {
            return NotFound();
        }

        if (!IsCaseDocumentTemplateApplicableToCase(template, clientCase))
        {
            return BadRequest(new { message = "Шаблон документа не привязан к типу этого дела." });
        }

        var documentType = NormalizeOptional(request.DocumentType) ?? template.DocumentType;
        var caseNumber = !string.IsNullOrWhiteSpace(clientCase.CaseNumber)
            ? clientCase.CaseNumber.Trim()
            : clientCase.Id.ToString();

        return Ok(new RenderedCaseDocumentTemplateDto
        {
            TemplateKey = template.Key,
            TemplateLabel = template.Title,
            DocumentType = documentType,
            FileName = $"{template.Title} - дело {caseNumber}.html",
            Html = BuildCaseDocumentTemplateHtml(clientCase, template, documentType),
        });
    }

    [HttpGet("document-types")]
    public async Task<ActionResult<IReadOnlyList<string>>> GetDocumentTypes(CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var storedTypes = await _db.ClientCaseDocuments
            .AsNoTracking()
            .Select(item => item.DocumentType)
            .Where(value => value != "")
            .Distinct()
            .ToListAsync(ct);

        var comparer = StringComparer.OrdinalIgnoreCase;
        var templateTypes = (await GetCaseDocumentTemplateLibraryAsync(ct))
            .Where(static item => item.IsActive)
            .Select(item => item.DocumentType);
        var result = DefaultCaseDocumentTypes
            .Concat(templateTypes)
            .Concat(storedTypes)
            .Select(NormalizeOptional)
            .Where(static value => value is not null)
            .Select(static value => value!)
            .Distinct(comparer)
            .OrderBy(value => value, StringComparer.Create(new CultureInfo("ru-RU"), ignoreCase: true))
            .ToList();

        return Ok(result);
    }

    [HttpGet("/api/case-calendar/events")]
    public async Task<ActionResult<IReadOnlyList<CaseCalendarEventDto>>> GetCaseCalendarEvents(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int? clientCaseId,
        [FromQuery] int? responsibleUserId,
        [FromQuery] bool includeTasks = true,
        CancellationToken ct = default)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var fromDate = (from ?? DateTime.UtcNow.Date.AddDays(-30)).Date;
        var toDate = (to ?? fromDate.AddDays(90)).Date;
        if (toDate < fromDate)
        {
            return BadRequest(new { message = "Дата окончания периода не может быть раньше даты начала." });
        }

        var events = await QueryCaseCalendarEventsAsync(visibility, fromDate, toDate, clientCaseId, responsibleUserId, includeTasks, ct);
        return Ok(events);
    }

    [HttpGet("/api/case-calendar/summary")]
    public async Task<ActionResult<CaseCalendarSummaryDto>> GetCaseCalendarSummary(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int? clientCaseId,
        [FromQuery] int? responsibleUserId,
        [FromQuery] bool includeTasks = true,
        CancellationToken ct = default)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var fromDate = (from ?? DateTime.UtcNow.Date.AddDays(-30)).Date;
        var toDate = (to ?? fromDate.AddDays(90)).Date;
        if (toDate < fromDate)
        {
            return BadRequest(new { message = "Дата окончания периода не может быть раньше даты начала." });
        }

        var events = await QueryCaseCalendarEventsAsync(visibility, fromDate, toDate, clientCaseId, responsibleUserId, includeTasks, ct);
        return Ok(BuildCaseCalendarSummary(events, fromDate, toDate));
    }

    [HttpPost("procedural-deadlines/calculate")]
    public ActionResult<ProceduralDeadlineCalculationDto> CalculateProceduralDeadlinePreview(
        [FromBody] ProceduralDeadlineCalculationRequest? request)
    {
        var result = ProceduralDeadlineCalculator.Calculate(request ?? new ProceduralDeadlineCalculationRequest());
        if (result.Error is not null)
        {
            return BadRequest(new { message = result.Error });
        }

        return Ok(new ProceduralDeadlineCalculationDto
        {
            DeadlineAt = result.DeadlineAt,
            Rule = result.Rule,
            MonthsToAdd = result.MonthsToAdd,
            DaysToAdd = result.DaysToAdd,
            RollForwardToWorkingDay = result.RollForwardToWorkingDay,
        });
    }

    [HttpGet("court-directory")]
    public async Task<ActionResult<IReadOnlyList<ClientCaseCourtDto>>> GetCourtDirectory(
        [FromQuery] bool includeInactive = false,
        CancellationToken ct = default)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var query = _db.ClientCaseCourts.AsNoTracking();
        if (!includeInactive)
        {
            query = query.Where(item => item.IsActive);
        }

        var courts = await query
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Name)
            .ToListAsync(ct);
        var items = courts.Select(MapCourt).ToList();

        return Ok(items);
    }

    [HttpPost("court-directory")]
    public async Task<ActionResult<ClientCaseCourtDto>> CreateCourtDirectoryItem(
        [FromBody] UpsertClientCaseCourtRequest? request,
        CancellationToken ct)
    {
        if (await RequireDocumentTemplateEditPermissionAsync(ct) is { } forbidden)
        {
            return forbidden;
        }

        var validation = ValidateCourtDirectoryRequest(request);
        if (validation.Error is not null)
        {
            return BadRequest(new { message = validation.Error });
        }

        var actorUserId = User.GetUserId();
        var actorName = User.GetDisplayName();
        var now = DateTime.UtcNow;
        var item = new ClientCaseCourt
        {
            Name = validation.Name,
            Instance = validation.Instance,
            Code = validation.Code,
            Url = validation.Url,
            Address = validation.Address,
            Contacts = validation.Contacts,
            IsActive = validation.IsActive,
            SortOrder = validation.SortOrder,
            CreatedAt = now,
            CreatedByUserId = actorUserId,
            CreatedByUserName = Truncate(actorName, 256),
            UpdatedAt = now,
            UpdatedByUserId = actorUserId,
            UpdatedByUserName = Truncate(actorName, 256),
        };

        _db.ClientCaseCourts.Add(item);
        _db.SetAuditReason($"Добавлен суд в справочник: {item.Name}");
        await _db.SaveChangesAsync(ct);

        return Created($"/api/v2/cases/court-directory/{item.Id}", MapCourt(item));
    }

    [HttpPut("court-directory/{courtId:int}")]
    public async Task<ActionResult<ClientCaseCourtDto>> UpdateCourtDirectoryItem(
        int courtId,
        [FromBody] UpsertClientCaseCourtRequest? request,
        CancellationToken ct)
    {
        if (await RequireDocumentTemplateEditPermissionAsync(ct) is { } forbidden)
        {
            return forbidden;
        }

        var item = await _db.ClientCaseCourts.FirstOrDefaultAsync(court => court.Id == courtId, ct);
        if (item is null)
        {
            return NotFound();
        }

        var validation = ValidateCourtDirectoryRequest(request);
        if (validation.Error is not null)
        {
            return BadRequest(new { message = validation.Error });
        }

        item.Name = validation.Name;
        item.Instance = validation.Instance;
        item.Code = validation.Code;
        item.Url = validation.Url;
        item.Address = validation.Address;
        item.Contacts = validation.Contacts;
        item.IsActive = validation.IsActive;
        item.SortOrder = validation.SortOrder;
        item.UpdatedAt = DateTime.UtcNow;
        item.UpdatedByUserId = User.GetUserId();
        item.UpdatedByUserName = Truncate(User.GetDisplayName(), 256);

        _db.SetAuditReason($"Обновлен суд в справочнике: {item.Name}");
        await _db.SaveChangesAsync(ct);

        return Ok(MapCourt(item));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var entity = await _db.ClientCases
            .WithCaseIncludes(includePayments: true)
            .ApplyClientCaseVisibility(visibility)
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (entity is not null)
        {
            await ApplyFinanceSummaryAsync([entity], ct);
            await ApplyTaskSummaryAsync([entity], ct);
        }

        if (entity is null)
        {
            var isHidden = await _db.ClientCases
                .AsNoTracking()
                .AnyAsync(item => item.Id == id && item.IsHiddenFromExecutor, ct);
            if (isHidden)
            {
                return StatusCode(StatusCodes.Status403Forbidden, new
                {
                    message = "Дело скрыто от исполнителя. Отправьте запрос доступа ответственному руководителю.",
                });
            }
        }

        return entity is not null ? Ok(entity) : NotFound();
    }

    [HttpGet("{id:int}/procedural-deadlines")]
    public async Task<ActionResult<IReadOnlyList<ClientCaseProceduralDeadlineDto>>> GetProceduralDeadlines(int id, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var exists = await _db.ClientCases
            .AsNoTracking()
            .ApplyClientCaseVisibility(visibility)
            .AnyAsync(item => item.Id == id, ct);
        if (!exists)
        {
            return NotFound();
        }

        var deadlines = await _db.ClientCaseProceduralDeadlines
            .AsNoTracking()
            .Where(item => item.ClientCaseId == id)
            .OrderBy(item => item.Status == "closed")
            .ThenBy(item => item.DueAt)
            .ThenBy(item => item.Id)
            .Select(item => MapProceduralDeadline(item))
            .ToListAsync(ct);

        return Ok(deadlines);
    }

    [HttpPost("{id:int}/procedural-deadlines")]
    public async Task<ActionResult<ClientCaseProceduralDeadlineDto>> CreateProceduralDeadline(
        int id,
        [FromBody] UpsertClientCaseProceduralDeadlineRequest? request,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var clientCase = await _db.ClientCases
            .Include(item => item.Client)
            .Include(item => item.LegalEntity)
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var validation = ValidateProceduralDeadlineRequest(request);
        if (validation.Error is not null)
        {
            return BadRequest(new { message = validation.Error });
        }

        var linkError = await ValidateProceduralDeadlineLinksAsync(id, request, ct);
        if (linkError is not null)
        {
            return BadRequest(new { message = linkError });
        }

        var actorUserId = User.GetUserId();
        var actorName = User.GetDisplayName();
        var now = DateTime.UtcNow;
        var deadline = new ClientCaseProceduralDeadline
        {
            ClientCaseId = id,
            DeadlineType = validation.DeadlineType,
            Title = validation.Title,
            Description = validation.Description,
            DueAt = validation.DueAt,
            AllDay = validation.AllDay,
            Status = validation.Status,
            WarningDaysBefore = validation.WarningDaysBefore,
            TaskJournalItemId = request?.TaskJournalItemId,
            ClientCaseDocumentId = request?.ClientCaseDocumentId,
            SourceType = validation.SourceType,
            SourceId = request?.SourceId,
            Notes = validation.Notes,
            CreatedAt = now,
            CreatedByUserId = actorUserId,
            CreatedByUserName = Truncate(actorName, 256),
            UpdatedAt = now,
            UpdatedByUserId = actorUserId,
            UpdatedByUserName = Truncate(actorName, 256),
        };

        _db.ClientCaseProceduralDeadlines.Add(deadline);
        AppendCaseEvent(clientCase, $"Добавлен процессуальный срок «{deadline.Title}» на {deadline.DueAt:dd.MM.yyyy}. Автор: {actorName}.");
        _db.SetAuditReason($"Добавлен процессуальный срок дела: {deadline.Title}");
        await _db.SaveChangesAsync(ct);

        await ClientCaseDeadlineTaskSyncService.SyncAsync(_db, id, actorUserId, actorName, ct);
        await LinkProceduralDeadlineTaskAsync(deadline.Id, ct);
        await SyncDeadlineCourtEventLinkAsync(deadline, null, null, actorUserId, actorName, ct);
        await AddProceduralDeadlineUserNotificationsAsync(clientCase, deadline, "created", actorUserId, actorName, ct);
        await _db.SaveChangesAsync(ct);

        return Created($"/api/v2/cases/{id}/procedural-deadlines/{deadline.Id}", MapProceduralDeadline(deadline));
    }

    [HttpPut("{id:int}/procedural-deadlines/{deadlineId:int}")]
    public async Task<ActionResult<ClientCaseProceduralDeadlineDto>> UpdateProceduralDeadline(
        int id,
        int deadlineId,
        [FromBody] UpsertClientCaseProceduralDeadlineRequest? request,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var clientCase = await _db.ClientCases
            .Include(item => item.Client)
            .Include(item => item.LegalEntity)
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var deadline = await _db.ClientCaseProceduralDeadlines
            .FirstOrDefaultAsync(item => item.Id == deadlineId && item.ClientCaseId == id, ct);
        if (deadline is null)
        {
            return NotFound();
        }

        var validation = ValidateProceduralDeadlineRequest(request);
        if (validation.Error is not null)
        {
            return BadRequest(new { message = validation.Error });
        }

        var linkError = await ValidateProceduralDeadlineLinksAsync(id, request, ct);
        if (linkError is not null)
        {
            return BadRequest(new { message = linkError });
        }

        var actorUserId = User.GetUserId();
        var actorName = User.GetDisplayName();
        var oldDueAt = deadline.DueAt;
        var oldStatus = deadline.Status;
        var oldSourceType = deadline.SourceType;
        var oldSourceId = deadline.SourceId;

        deadline.DeadlineType = validation.DeadlineType;
        deadline.Title = validation.Title;
        deadline.Description = validation.Description;
        deadline.DueAt = validation.DueAt;
        deadline.AllDay = validation.AllDay;
        deadline.Status = validation.Status;
        deadline.WarningDaysBefore = validation.WarningDaysBefore;
        deadline.TaskJournalItemId = request?.TaskJournalItemId;
        deadline.ClientCaseDocumentId = request?.ClientCaseDocumentId;
        deadline.SourceType = validation.SourceType;
        deadline.SourceId = request?.SourceId;
        deadline.Notes = validation.Notes;
        deadline.UpdatedAt = DateTime.UtcNow;
        deadline.UpdatedByUserId = actorUserId;
        deadline.UpdatedByUserName = Truncate(actorName, 256);

        if (deadline.Status == "closed" && oldStatus != "closed")
        {
            deadline.ClosedAt = DateTime.UtcNow;
            deadline.ClosedByUserId = actorUserId;
            deadline.ClosedByUserName = Truncate(actorName, 256);
            deadline.CloseReason = Truncate(NormalizeOptional(request?.CloseReason) ?? string.Empty, 1000);
        }
        else if (deadline.Status != "closed")
        {
            deadline.ClosedAt = null;
            deadline.ClosedByUserId = null;
            deadline.ClosedByUserName = string.Empty;
            deadline.CloseReason = string.Empty;
        }

        var moveSuffix = oldDueAt.Date == deadline.DueAt.Date
            ? string.Empty
            : $" Перенос: {oldDueAt:dd.MM.yyyy} -> {deadline.DueAt:dd.MM.yyyy}.";
        AppendCaseEvent(clientCase, $"Процессуальный срок «{deadline.Title}» обновлен.{moveSuffix} Изменил: {actorName}.");
        _db.SetAuditReason($"Обновлен процессуальный срок дела: {deadline.Title}");
        await _db.SaveChangesAsync(ct);

        await ClientCaseDeadlineTaskSyncService.SyncAsync(_db, id, actorUserId, actorName, ct);
        await LinkProceduralDeadlineTaskAsync(deadline.Id, ct);
        await SyncDeadlineCourtEventLinkAsync(deadline, oldSourceType, oldSourceId, actorUserId, actorName, ct);
        await AddProceduralDeadlineUserNotificationsAsync(clientCase, deadline, "updated", actorUserId, actorName, ct);
        await _db.SaveChangesAsync(ct);

        return Ok(MapProceduralDeadline(deadline));
    }

    [HttpPost("{id:int}/procedural-deadlines/{deadlineId:int}/close")]
    public async Task<ActionResult<ClientCaseProceduralDeadlineDto>> CloseProceduralDeadline(
        int id,
        int deadlineId,
        [FromBody] CloseClientCaseProceduralDeadlineRequest? request,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var clientCase = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var deadline = await _db.ClientCaseProceduralDeadlines
            .FirstOrDefaultAsync(item => item.Id == deadlineId && item.ClientCaseId == id, ct);
        if (deadline is null)
        {
            return NotFound();
        }

        var actorUserId = User.GetUserId();
        var actorName = User.GetDisplayName();
        deadline.Status = "closed";
        deadline.ClosedAt = DateTime.UtcNow;
        deadline.ClosedByUserId = actorUserId;
        deadline.ClosedByUserName = Truncate(actorName, 256);
        deadline.CloseReason = Truncate(NormalizeOptional(request?.Reason) ?? string.Empty, 1000);
        deadline.UpdatedAt = deadline.ClosedAt;
        deadline.UpdatedByUserId = actorUserId;
        deadline.UpdatedByUserName = Truncate(actorName, 256);

        AppendCaseEvent(clientCase, $"Процессуальный срок «{deadline.Title}» закрыт. Закрыл: {actorName}.{FormatOptionalComment(deadline.CloseReason)}");
        _db.SetAuditReason($"Закрыт процессуальный срок дела: {deadline.Title}");
        await _db.SaveChangesAsync(ct);

        await ClientCaseDeadlineTaskSyncService.SyncAsync(_db, id, actorUserId, actorName, ct);
        await AddProceduralDeadlineUserNotificationsAsync(clientCase, deadline, "closed", actorUserId, actorName, ct);
        await _db.SaveChangesAsync(ct);
        return Ok(MapProceduralDeadline(deadline));
    }

    [HttpGet("{id:int}/court-events")]
    public async Task<ActionResult<IReadOnlyList<ClientCaseCourtEventDto>>> GetCourtEvents(int id, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var exists = await _db.ClientCases
            .AsNoTracking()
            .ApplyClientCaseVisibility(visibility)
            .AnyAsync(item => item.Id == id, ct);
        if (!exists)
        {
            return NotFound();
        }

        var items = await _db.ClientCaseCourtEvents
            .AsNoTracking()
            .Where(item => item.ClientCaseId == id)
            .OrderBy(item => item.ScheduledAt)
            .ThenBy(item => item.Id)
            .Select(item => MapCourtEvent(item))
            .ToListAsync(ct);

        return Ok(items);
    }

    [HttpPost("{id:int}/court-events")]
    public async Task<ActionResult<ClientCaseCourtEventDto>> CreateCourtEvent(
        int id,
        [FromBody] UpsertClientCaseCourtEventRequest? request,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var clientCase = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var court = await ResolveCourtDirectoryItemForRequestAsync(request?.CourtId, ct);
        if (request?.CourtId is not null && court is null)
        {
            return BadRequest(new { message = "Судебная инстанция не найдена в справочнике." });
        }

        var validation = ValidateCourtEventRequest(request, clientCase, court);
        if (validation.Error is not null)
        {
            return BadRequest(new { message = validation.Error });
        }

        var linkError = await ValidateCourtEventLinksAsync(id, request, ct);
        if (linkError is not null)
        {
            return BadRequest(new { message = linkError });
        }

        var actorUserId = User.GetUserId();
        var actorName = User.GetDisplayName();
        var now = DateTime.UtcNow;
        var courtEvent = new ClientCaseCourtEvent
        {
            ClientCaseId = id,
            EventType = validation.EventType,
            Title = validation.Title,
            Description = validation.Description,
            ScheduledAt = validation.ScheduledAt,
            AllDay = validation.AllDay,
            CourtInstance = validation.CourtInstance,
            CourtId = court?.Id,
            CourtName = validation.CourtName,
            CourtCaseNumber = validation.CourtCaseNumber,
            Courtroom = validation.Courtroom,
            JudgeName = validation.JudgeName,
            SecretaryName = validation.SecretaryName,
            MovementStatus = validation.MovementStatus,
            ResultSummary = validation.ResultSummary,
            SourceType = validation.SourceType,
            ClientCaseDocumentId = request?.ClientCaseDocumentId,
            ProceduralDeadlineId = request?.ProceduralDeadlineId,
            CreatedAt = now,
            CreatedByUserId = actorUserId,
            CreatedByUserName = Truncate(actorName, 256),
            UpdatedAt = now,
            UpdatedByUserId = actorUserId,
            UpdatedByUserName = Truncate(actorName, 256),
        };

        _db.ClientCaseCourtEvents.Add(courtEvent);
        AppendCaseEvent(clientCase, $"Добавлено судебное событие «{courtEvent.Title}» на {courtEvent.ScheduledAt:dd.MM.yyyy HH:mm}. Автор: {actorName}.");
        _db.SetAuditReason($"Добавлено судебное событие дела: {courtEvent.Title}");
        await _db.SaveChangesAsync(ct);

        AddCourtEventHistory(courtEvent, "created", $"Событие создано: {courtEvent.Title}.", actorUserId, actorName);
        await SyncCourtEventDeadlineSourceAsync(courtEvent, null, actorUserId, actorName, ct);
        await AddCourtEventUserNotificationsAsync(clientCase, courtEvent, "created", actorUserId, actorName, ct);
        await _db.SaveChangesAsync(ct);

        return Created($"/api/v2/cases/{id}/court-events/{courtEvent.Id}", MapCourtEvent(courtEvent));
    }

    [HttpPut("{id:int}/court-events/{courtEventId:int}")]
    public async Task<ActionResult<ClientCaseCourtEventDto>> UpdateCourtEvent(
        int id,
        int courtEventId,
        [FromBody] UpsertClientCaseCourtEventRequest? request,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var clientCase = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var courtEvent = await _db.ClientCaseCourtEvents
            .FirstOrDefaultAsync(item => item.Id == courtEventId && item.ClientCaseId == id, ct);
        if (courtEvent is null)
        {
            return NotFound();
        }

        var court = await ResolveCourtDirectoryItemForRequestAsync(request?.CourtId, ct);
        if (request?.CourtId is not null && court is null)
        {
            return BadRequest(new { message = "Судебная инстанция не найдена в справочнике." });
        }

        var validation = ValidateCourtEventRequest(request, clientCase, court);
        if (validation.Error is not null)
        {
            return BadRequest(new { message = validation.Error });
        }

        var linkError = await ValidateCourtEventLinksAsync(id, request, ct);
        if (linkError is not null)
        {
            return BadRequest(new { message = linkError });
        }

        var actorUserId = User.GetUserId();
        var actorName = User.GetDisplayName();
        var oldProceduralDeadlineId = courtEvent.ProceduralDeadlineId;
        courtEvent.EventType = validation.EventType;
        courtEvent.Title = validation.Title;
        courtEvent.Description = validation.Description;
        courtEvent.ScheduledAt = validation.ScheduledAt;
        courtEvent.AllDay = validation.AllDay;
        courtEvent.CourtInstance = validation.CourtInstance;
        courtEvent.CourtId = court?.Id;
        courtEvent.CourtName = validation.CourtName;
        courtEvent.CourtCaseNumber = validation.CourtCaseNumber;
        courtEvent.Courtroom = validation.Courtroom;
        courtEvent.JudgeName = validation.JudgeName;
        courtEvent.SecretaryName = validation.SecretaryName;
        courtEvent.MovementStatus = validation.MovementStatus;
        courtEvent.ResultSummary = validation.ResultSummary;
        courtEvent.SourceType = validation.SourceType;
        courtEvent.ClientCaseDocumentId = request?.ClientCaseDocumentId;
        courtEvent.ProceduralDeadlineId = request?.ProceduralDeadlineId;
        courtEvent.UpdatedAt = DateTime.UtcNow;
        courtEvent.UpdatedByUserId = actorUserId;
        courtEvent.UpdatedByUserName = Truncate(actorName, 256);

        AppendCaseEvent(clientCase, $"Обновлено судебное событие «{courtEvent.Title}». Автор: {actorName}.");
        _db.SetAuditReason($"Обновлено судебное событие дела: {courtEvent.Title}");
        AddCourtEventHistory(courtEvent, "updated", $"Событие обновлено: {courtEvent.Title}.", actorUserId, actorName);
        await SyncCourtEventDeadlineSourceAsync(courtEvent, oldProceduralDeadlineId, actorUserId, actorName, ct);
        await AddCourtEventUserNotificationsAsync(clientCase, courtEvent, "updated", actorUserId, actorName, ct);
        await _db.SaveChangesAsync(ct);

        return Ok(MapCourtEvent(courtEvent));
    }

    [HttpGet("{id:int}/court-events/{courtEventId:int}/history")]
    public async Task<ActionResult<IReadOnlyList<ClientCaseCourtEventHistoryDto>>> GetCourtEventHistory(
        int id,
        int courtEventId,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var exists = await _db.ClientCaseCourtEvents
            .AsNoTracking()
            .Where(item => item.Id == courtEventId && item.ClientCaseId == id)
            .AnyAsync(item => _db.ClientCases
                .ApplyClientCaseVisibility(visibility)
                .Any(clientCase => clientCase.Id == item.ClientCaseId), ct);
        if (!exists)
        {
            return NotFound();
        }

        var history = await _db.ClientCaseCourtEventHistory
            .AsNoTracking()
            .Where(item => item.ClientCaseCourtEventId == courtEventId && item.ClientCaseId == id)
            .OrderByDescending(item => item.OccurredAt)
            .ThenByDescending(item => item.Id)
            .Select(item => MapCourtEventHistory(item))
            .ToListAsync(ct);

        return Ok(history);
    }

    [HttpGet("{id:int}/court-parties")]
    public async Task<ActionResult<IReadOnlyList<ClientCaseCourtPartyDto>>> GetCourtParties(
        int id,
        [FromQuery] bool includeArchived,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var exists = await _db.ClientCases
            .AsNoTracking()
            .ApplyClientCaseVisibility(visibility)
            .AnyAsync(item => item.Id == id, ct);
        if (!exists)
        {
            return NotFound();
        }

        var query = _db.ClientCaseCourtParties
            .AsNoTracking()
            .Where(item => item.ClientCaseId == id);
        if (!includeArchived)
        {
            query = query.Where(item => !item.IsArchived);
        }

        var items = await query
            .OrderBy(item => item.IsArchived)
            .ThenBy(item => item.Side)
            .ThenBy(item => item.DisplayName)
            .ThenBy(item => item.Id)
            .Select(item => MapCourtParty(item))
            .ToListAsync(ct);

        return Ok(items);
    }

    [HttpPost("{id:int}/court-parties")]
    public async Task<ActionResult<ClientCaseCourtPartyDto>> CreateCourtParty(
        int id,
        [FromBody] UpsertClientCaseCourtPartyRequest? request,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var clientCase = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var validation = await ValidateCourtPartyRequestAsync(id, request, ct);
        if (validation.Error is not null)
        {
            return BadRequest(new { message = validation.Error });
        }

        var actorUserId = User.GetUserId();
        var actorName = User.GetDisplayName();
        var now = DateTime.UtcNow;
        var party = new ClientCaseCourtParty
        {
            ClientCaseId = id,
            RelatedPartyId = validation.RelatedPartyId,
            DisplayName = validation.DisplayName,
            Side = validation.Side,
            ProcessRole = validation.ProcessRole,
            RepresentativeName = validation.RepresentativeName,
            Notes = validation.Notes,
            CreatedAt = now,
            CreatedByUserId = actorUserId,
            CreatedByUserName = Truncate(actorName, 256),
            UpdatedAt = now,
            UpdatedByUserId = actorUserId,
            UpdatedByUserName = Truncate(actorName, 256),
        };

        _db.ClientCaseCourtParties.Add(party);
        AppendCaseEvent(clientCase, $"Добавлена сторона процесса: {party.DisplayName} ({ResolveCourtPartySideLabel(party.Side)}). Автор: {actorName}.");
        _db.SetAuditReason($"Добавлена сторона процесса по делу: {party.DisplayName}");
        await _db.SaveChangesAsync(ct);

        return Created($"/api/v2/cases/{id}/court-parties/{party.Id}", MapCourtParty(party));
    }

    [HttpDelete("{id:int}/court-parties/{partyId:int}")]
    public async Task<IActionResult> ArchiveCourtParty(int id, int partyId, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var clientCase = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var party = await _db.ClientCaseCourtParties
            .FirstOrDefaultAsync(item => item.Id == partyId && item.ClientCaseId == id, ct);
        if (party is null)
        {
            return NotFound();
        }

        if (!party.IsArchived)
        {
            party.IsArchived = true;
            party.UpdatedAt = DateTime.UtcNow;
            party.UpdatedByUserId = User.GetUserId();
            party.UpdatedByUserName = Truncate(User.GetDisplayName(), 256);
            AppendCaseEvent(clientCase, $"Сторона процесса архивирована: {party.DisplayName}. Автор: {User.GetDisplayName()}.");
            _db.SetAuditReason($"Сторона процесса архивирована: {party.DisplayName}");
            await _db.SaveChangesAsync(ct);
        }

        return NoContent();
    }

    [HttpPost("{id:int}/access-request")]
    public async Task<ActionResult<ClientCaseAccessRequestDto>> RequestAccess(
        int id,
        [FromBody] RequestCaseAccessRequest? request,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var currentUserId = User.GetUserId();
        if (!currentUserId.HasValue)
        {
            return Forbid();
        }

        var clientCase = await _db.ClientCases
            .WithCaseIncludes()
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var actorName = User.GetDisplayName();
        var reason = NormalizeOptional(request?.Reason);
        reason = reason is null ? string.Empty : Truncate(NormalizeHistoryText(reason), 1000);
        var now = DateTime.UtcNow;

        clientCase.AccessRequestedAt = now;
        clientCase.AccessRequestedByUserId = currentUserId.Value;
        clientCase.AccessRequestedByUserName = Truncate(actorName, 256);
        clientCase.AccessRequestReason = reason;

        var reasonSuffix = string.IsNullOrWhiteSpace(reason) ? string.Empty : $" Причина: {reason}.";
        AppendCaseEvent(clientCase, $"Запрошен доступ к делу. Автор: {actorName}.{reasonSuffix}");
        _db.SetAuditReason($"Запрошен доступ к делу: {actorName}");

        var managerUserIds = await ResolveCaseAccessManagerUserIdsAsync(clientCase, currentUserId.Value, ct);
        foreach (var managerUserId in managerUserIds)
        {
            await AddCaseUserNotificationAsync(
                managerUserId,
                UserNotificationTypes.CaseAccessRequested,
                $"case:{clientCase.Id}:access-request:{now:yyyyMMddHHmmssfff}:{currentUserId.Value}",
                $"Запрошен доступ к делу: {BuildCaseNotificationTitle(clientCase)}",
                BuildCaseAccessRequestNotificationBody(clientCase, actorName, reason),
                "violet",
                clientCase,
                new
                {
                    requestedByUserId = currentUserId.Value,
                    requestedByUserName = actorName,
                    reason,
                    requestedAt = now,
                },
                ct);
        }

        await _db.SaveChangesAsync(ct);

        return Ok(new ClientCaseAccessRequestDto
        {
            ClientCaseId = clientCase.Id,
            RequestedAt = now,
            RequestedByUserId = currentUserId.Value,
            RequestedByUserName = actorName,
            Reason = reason,
            IsHiddenFromExecutor = clientCase.IsHiddenFromExecutor,
        });
    }

    [HttpGet("access-requests")]
    public async Task<ActionResult<IReadOnlyList<ClientCaseAccessRequestListItemDto>>> GetAccessRequests(CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var items = await ApplyClientCaseAccessManagementVisibility(_db.ClientCases.AsQueryable(), visibility)
            .WithCaseIncludes()
            .Where(item => item.AccessRequestedAt.HasValue)
            .OrderByDescending(item => item.AccessRequestedAt)
            .Take(100)
            .AsNoTracking()
            .ToListAsync(ct);

        return Ok(items.Select(MapClientCaseAccessRequest).ToList());
    }

    [HttpPost("{id:int}/access-request/resolve")]
    public async Task<ActionResult<ClientCaseAccessRequestResolutionDto>> ResolveAccessRequest(
        int id,
        [FromBody] ResolveCaseAccessRequest? request,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var clientCase = await ApplyClientCaseAccessManagementVisibility(_db.ClientCases.AsQueryable(), visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        if (!clientCase.AccessRequestedAt.HasValue)
        {
            return BadRequest(new { message = "По этому делу нет активного запроса доступа." });
        }

        var requestedByUserId = clientCase.AccessRequestedByUserId;
        var requestedByUserName = clientCase.AccessRequestedByUserName;
        var requestReason = clientCase.AccessRequestReason;
        var grant = request?.Grant == true;
        var actorName = User.GetDisplayName();
        var actorUserId = User.GetUserId();
        var resolvedAt = DateTime.UtcNow;
        var comment = NormalizeOptional(request?.Comment);
        comment = comment is null ? string.Empty : Truncate(NormalizeHistoryText(comment), 1000);
        var commentSuffix = string.IsNullOrWhiteSpace(comment) ? string.Empty : $" Комментарий: {comment}.";

        if (grant)
        {
            ApplyHiddenFromExecutorMetadata(clientCase, isHidden: false, User.GetUserId(), actorName);
            AppendCaseEvent(clientCase, $"Запрос доступа разрешен. Решение: {actorName}.{commentSuffix}");
            _db.SetAuditReason($"Запрос доступа к делу разрешен: {actorName}");
        }
        else
        {
            clientCase.AccessRequestedAt = null;
            clientCase.AccessRequestedByUserId = null;
            clientCase.AccessRequestedByUserName = string.Empty;
            clientCase.AccessRequestReason = string.Empty;
            AppendCaseEvent(clientCase, $"Запрос доступа отклонен. Решение: {actorName}.{commentSuffix}");
            _db.SetAuditReason($"Запрос доступа к делу отклонен: {actorName}");
        }

        if (requestedByUserId.HasValue && (!actorUserId.HasValue || requestedByUserId.Value != actorUserId.Value))
        {
            await AddCaseUserNotificationAsync(
                requestedByUserId.Value,
                UserNotificationTypes.CaseAccessResolved,
                $"case:{clientCase.Id}:access-request:resolved:{(grant ? "granted" : "denied")}:{resolvedAt:yyyyMMddHHmmssfff}:{requestedByUserId.Value}",
                grant
                    ? $"Доступ к делу разрешен: {BuildCaseNotificationTitle(clientCase)}"
                    : $"Доступ к делу отклонен: {BuildCaseNotificationTitle(clientCase)}",
                BuildCaseAccessResolutionNotificationBody(clientCase, grant, actorName, comment),
                grant ? "emerald" : "orange",
                clientCase,
                new
                {
                    requestedByUserId,
                    requestedByUserName,
                    requestReason,
                    resolvedByUserName = actorName,
                    resolvedAt,
                    granted = grant,
                    comment,
                },
                ct);
        }

        await _db.SaveChangesAsync(ct);

        return Ok(new ClientCaseAccessRequestResolutionDto
        {
            ClientCaseId = clientCase.Id,
            Granted = grant,
            ResolvedAt = resolvedAt,
            ResolvedByUserName = actorName,
            IsHiddenFromExecutor = clientCase.IsHiddenFromExecutor,
        });
    }

    [HttpGet("{id:int}/timeline")]
    public async Task<ActionResult<IReadOnlyList<ClientCaseTimelineEntryDto>>> GetTimeline(int id, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var clientCase = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var timeline = SplitCaseHistory(clientCase.EventHistory)
            .Select((line, index) => MapTimelineEntry(clientCase.Id, line, index))
            .OrderByDescending(item => item.OccurredAt ?? DateTime.MinValue)
            .ThenByDescending(item => item.Id)
            .ToList();

        return Ok(timeline);
    }

    [HttpGet("{id:int}/conversation")]
    public async Task<ActionResult<IReadOnlyList<ClientCaseConversationEntryDto>>> GetConversation(int id, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var clientCase = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        return Ok(MapCaseConversation(clientCase.Id, clientCase.EventHistory));
    }

    [HttpGet("{id:int}/summary")]
    public async Task<ActionResult<ClientCaseSummaryDto>> GetSummary(int id, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var clientCase = await _db.ClientCases
            .WithCaseIncludes(includePayments: true)
            .ApplyClientCaseVisibility(visibility)
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        await ApplyFinanceSummaryAsync([clientCase], ct);
        await ApplyTaskSummaryAsync([clientCase], ct);

        return Ok(MapCaseSummary(clientCase));
    }

    [HttpGet("{id:int}/finance")]
    public async Task<ActionResult<ClientCaseFinanceDetailDto>> GetFinance(int id, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var clientCase = await _db.ClientCases
            .WithCaseIncludes()
            .ApplyClientCaseVisibility(visibility)
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var payments = await _db.Payments
            .AsNoTracking()
            .Include(payment => payment.InvoiceClientCases)
            .Include(payment => payment.Contract)
            .Include(payment => payment.PaymentSource)
            .Where(payment => payment.Status != PaymentStatus.Cancelled)
            .Where(payment =>
                (payment.ClientCaseId.HasValue && payment.ClientCaseId.Value == id) ||
                payment.InvoiceClientCases.Any(link => link.ClientCaseId == id))
            .ToListAsync(ct);

        var visiblePayments = InvoiceManagedPaymentRules.FilterVisiblePayments(payments)
            .OrderByDescending(ResolveFinancePaymentSortDate)
            .ThenByDescending(payment => payment.Id)
            .ToList();

        var paymentIds = visiblePayments
            .Select(payment => payment.Id)
            .Distinct()
            .ToArray();
        var invoicePaymentIds = visiblePayments
            .Where(payment => payment.DocumentKind == DocumentKind.Invoice)
            .Select(payment => payment.Id)
            .Distinct()
            .ToArray();
        var linkedInvoicePaymentIds = paymentIds
            .Concat(invoicePaymentIds)
            .Distinct()
            .ToArray();

        var linkedContracts = await LoadFinanceContractsAsync(visiblePayments, linkedInvoicePaymentIds, paymentIds, ct);

        return Ok(new ClientCaseFinanceDetailDto
        {
            CaseId = clientCase.Id,
            CaseTitle = clientCase.Title,
            OwnerName = ResolveCaseOwnerName(clientCase),
            Totals = BuildFinanceTotals(clientCase, visiblePayments),
            Calendar = BuildFinanceCalendar(visiblePayments),
            Invoices = BuildFinanceInvoices(visiblePayments, linkedContracts),
            Payments = visiblePayments.Select(MapFinancePayment).ToList(),
            Contracts = linkedContracts.Values
                .OrderByDescending(contract => contract.Date)
                .ThenBy(contract => contract.Number)
                .Select(MapFinanceContract)
                .ToList(),
        });
    }

    [HttpGet("{id:int}/documents")]
    public async Task<ActionResult<IReadOnlyList<ClientCaseDocumentDto>>> GetDocuments(int id, CancellationToken ct)
    {
        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var clientCase = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var documents = await _db.ClientCaseDocuments
            .AsNoTracking()
            .Where(item => item.ClientCaseId == id)
            .OrderBy(item => item.DocumentType)
            .ThenBy(item => item.OriginalFileName)
            .ThenByDescending(item => item.Version)
            .ThenByDescending(item => item.Id)
            .ToListAsync(ct);

        var templates = await GetCaseDocumentTemplateLibraryAsync(ct);
        return Ok(MapDocuments(documents, templates));
    }

    [HttpGet("/api/case-documents")]
    public async Task<IActionResult> GetDocumentRegistry(
        [FromQuery] int? clientCaseId,
        [FromQuery] string? search,
        [FromQuery] string? documentType,
        [FromQuery] string? status,
        [FromQuery] int? sourceTaskId,
        [FromQuery] int? sourceEventId = null,
        [FromQuery] int? sourceWorkOrderId = null,
        [FromQuery] int? sourceInvoiceId = null,
        [FromQuery] string? sourceStage = null,
        [FromQuery] bool latestOnly = true,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxRegistryPageSize);

        var visibleCaseIds = _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .Select(item => item.Id);
        var query = _db.ClientCaseDocuments
            .AsNoTracking()
            .Where(item => visibleCaseIds.Contains(item.ClientCaseId));

        if (clientCaseId.HasValue)
        {
            query = query.Where(item => item.ClientCaseId == clientCaseId.Value);
        }

        var normalizedSearch = NormalizeOptional(search);
        if (normalizedSearch is not null)
        {
            query = query.Where(item =>
                item.OriginalFileName.Contains(normalizedSearch) ||
                item.DocumentType.Contains(normalizedSearch) ||
                item.Status.Contains(normalizedSearch) ||
                item.DocumentKey.Contains(normalizedSearch));
        }

        var normalizedType = NormalizeOptional(documentType);
        if (normalizedType is not null)
        {
            query = query.Where(item => item.DocumentType == normalizedType);
        }

        var normalizedStatus = NormalizeOptional(status);
        if (normalizedStatus is not null)
        {
            query = query.Where(item => item.Status == normalizedStatus);
        }

        if (sourceTaskId.HasValue)
        {
            var prefix = $"task-{sourceTaskId.Value}-";
            query = query.Where(item => EF.Functions.Like(item.DocumentKey, prefix + "%"));
        }

        if (sourceEventId.HasValue)
        {
            var key = BuildCourtEventDocumentKey(sourceEventId.Value);
            query = query.Where(item => item.DocumentKey == key);
        }

        if (sourceWorkOrderId.HasValue)
        {
            var key = BuildWorkOrderDocumentKey(sourceWorkOrderId.Value);
            query = query.Where(item => item.DocumentKey == key);
        }

        if (sourceInvoiceId.HasValue)
        {
            var key = BuildInvoiceDocumentKey(sourceInvoiceId.Value);
            query = query.Where(item => item.DocumentKey == key);
        }

        var normalizedSourceStage = NormalizeDocumentKey(sourceStage);
        if (normalizedSourceStage is not null)
        {
            var prefix = $"stage-{normalizedSourceStage}-";
            query = query.Where(item => EF.Functions.Like(item.DocumentKey, prefix + "%"));
        }

        if (latestOnly)
        {
            query = query.Where(item => !_db.ClientCaseDocuments.Any(other =>
                other.ClientCaseId == item.ClientCaseId &&
                other.DocumentKey == item.DocumentKey &&
                other.Version > item.Version));
        }

        var total = await query.CountAsync(ct);
        var documents = await query
            .OrderByDescending(item => item.UploadedAt)
            .ThenByDescending(item => item.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
        var items = await MapDocumentsWithLatestAsync(documents, ct);

        return Ok(new { items, total, page, pageSize });
    }

    [HttpGet("/api/case-documents/{documentId:int}")]
    public async Task<ActionResult<ClientCaseDocumentDto>> GetDocumentById(int documentId, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var document = await _db.ClientCaseDocuments
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == documentId, ct);
        if (document is null)
        {
            return NotFound();
        }

        var canViewCase = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .AsNoTracking()
            .AnyAsync(item => item.Id == document.ClientCaseId, ct);
        if (!canViewCase)
        {
            return NotFound();
        }

        var latestVersion = await _db.ClientCaseDocuments
            .Where(item => item.ClientCaseId == document.ClientCaseId && item.DocumentKey == document.DocumentKey)
            .MaxAsync(item => (int?)item.Version, ct) ?? document.Version;

        var templates = await GetCaseDocumentTemplateLibraryAsync(ct);
        return Ok(MapDocument(document, document.Version == latestVersion, templates));
    }

    [HttpPost("{id:int}/documents")]
    [RequestSizeLimit(MaxCaseDocumentBytes)]
    public async Task<ActionResult<ClientCaseDocumentDto>> UploadDocument(
        int id,
        [FromForm] IFormFile? file,
        [FromForm] string? documentType,
        [FromForm] string? status,
        [FromForm] string? documentKey,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var clientCase = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        return await UploadDocumentVersionCore(
            clientCase,
            file,
            documentType,
            status,
            documentKey,
            requireExistingDocument: false,
            ct);
    }

    [HttpPost("/api/case-documents/{documentId:int}/versions")]
    [RequestSizeLimit(MaxCaseDocumentBytes)]
    public async Task<ActionResult<ClientCaseDocumentDto>> UploadDocumentVersionById(
        int documentId,
        [FromForm] IFormFile? file,
        [FromForm] string? documentType,
        [FromForm] string? status,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var baseDocument = await _db.ClientCaseDocuments
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == documentId, ct);
        if (baseDocument is null)
        {
            return NotFound();
        }

        var clientCase = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == baseDocument.ClientCaseId, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        return await UploadDocumentVersionCore(
            clientCase,
            file,
            NormalizeOptional(documentType) ?? baseDocument.DocumentType,
            NormalizeOptional(status) ?? baseDocument.Status,
            baseDocument.DocumentKey,
            requireExistingDocument: true,
            ct);
    }

    [HttpPost("{id:int}/documents/generate-template")]
    public async Task<ActionResult<ClientCaseDocumentDto>> GenerateDocumentTemplate(
        int id,
        [FromBody] GenerateCaseDocumentTemplateRequest? request,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var clientCase = await _db.ClientCases
            .WithCaseIncludes()
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var templateDefinitionResult = await ResolveCaseDocumentTemplateDefinitionAsync(request, ct);
        if (templateDefinitionResult.Error is not null)
        {
            return templateDefinitionResult.NotFound
                ? NotFound(new { message = templateDefinitionResult.Error })
                : BadRequest(new { message = templateDefinitionResult.Error });
        }

        if (!IsCaseDocumentTemplateApplicableToCase(templateDefinitionResult.Template, clientCase))
        {
            return BadRequest(new { message = "Шаблон документа не привязан к типу этого дела." });
        }

        var template = BuildCaseDocumentTemplate(clientCase, request, templateDefinitionResult.Template);
        var latestVersion = await _db.ClientCaseDocuments
            .Where(item => item.ClientCaseId == id && item.DocumentKey == template.DocumentKey)
            .MaxAsync(item => (int?)item.Version, ct) ?? 0;
        var version = latestVersion + 1;
        var safeFileName = SanitizeFileName(template.FileName);
        var storedFileName = BuildStoredFileName(template.DocumentKey, version, safeFileName);
        var relativePath = _caseDocumentStorage.BuildRelativePath(id, storedFileName);

        var content = Encoding.UTF8.GetBytes(template.Html);
        try
        {
            await _caseDocumentStorage.SaveAsync(relativePath, content, ct);

            var document = new ClientCaseDocument
            {
                ClientCaseId = id,
                DocumentKey = template.DocumentKey,
                Version = version,
                DocumentType = template.DocumentType,
                Status = template.Status,
                OriginalFileName = safeFileName,
                StoredFileName = storedFileName,
                StoragePath = relativePath,
                ContentType = "text/html; charset=utf-8",
                SizeBytes = content.LongLength,
                UploadedByUserId = User.GetUserId(),
                UploadedByName = User.GetDisplayName(),
                UploadedAt = DateTime.UtcNow,
            };

            _db.ClientCaseDocuments.Add(document);
            AppendCaseEvent(
                clientCase,
                $"Сформирован шаблон «{document.OriginalFileName}»: тип «{document.DocumentType}», статус «{document.Status}», версия {document.Version}. Автор: {User.GetDisplayName()}.");
            await _db.SaveChangesAsync(ct);
            AddCaseDocumentAuditEvent(clientCase, document, "Generated", $"Сформирован документ из шаблона «{document.OriginalFileName}».");
            await _db.SaveChangesAsync(ct);

            return Created($"/api/v2/cases/{id}/documents/{document.Id}", MapDocument(document, true, [templateDefinitionResult.Template]));
        }
        catch
        {
            _caseDocumentStorage.DeleteIfExists(relativePath);
            throw;
        }
    }

    [HttpGet("{id:int}/documents/{documentId:int}/download")]
    public async Task<IActionResult> DownloadDocument(int id, int documentId, CancellationToken ct)
    {
        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var clientCase = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var document = await _db.ClientCaseDocuments
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == documentId && item.ClientCaseId == id, ct);
        if (document is null)
        {
            return NotFound();
        }

        if (!_caseDocumentStorage.Exists(document.StoragePath))
        {
            return NotFound();
        }

        var fullPath = _caseDocumentStorage.ResolvePath(document.StoragePath);
        return PhysicalFile(fullPath, document.ContentType, document.OriginalFileName);
    }

    [HttpGet("/api/case-documents/{documentId:int}/download")]
    public async Task<IActionResult> DownloadDocumentById(int documentId, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var document = await _db.ClientCaseDocuments
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == documentId, ct);
        if (document is null)
        {
            return NotFound();
        }

        var canViewCase = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .AsNoTracking()
            .AnyAsync(item => item.Id == document.ClientCaseId, ct);
        if (!canViewCase || !_caseDocumentStorage.Exists(document.StoragePath))
        {
            return NotFound();
        }

        var fullPath = _caseDocumentStorage.ResolvePath(document.StoragePath);
        return PhysicalFile(fullPath, document.ContentType, document.OriginalFileName);
    }

    [HttpPatch("/api/case-documents/{documentId:int}")]
    public async Task<ActionResult<ClientCaseDocumentDto>> UpdateDocumentById(
        int documentId,
        [FromBody] UpdateCaseDocumentRequest? request,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var document = await _db.ClientCaseDocuments
            .FirstOrDefaultAsync(item => item.Id == documentId, ct);
        if (document is null)
        {
            return NotFound();
        }

        var clientCase = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == document.ClientCaseId, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var dto = await UpdateDocumentCore(clientCase, document, request, ct);
        return Ok(dto);
    }

    [HttpPatch("{id:int}/documents/{documentId:int}")]
    public async Task<ActionResult<ClientCaseDocumentDto>> UpdateDocument(
        int id,
        int documentId,
        [FromBody] UpdateCaseDocumentRequest? request,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var clientCase = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var document = await _db.ClientCaseDocuments
            .FirstOrDefaultAsync(item => item.Id == documentId && item.ClientCaseId == id, ct);
        if (document is null)
        {
            return NotFound();
        }

        var dto = await UpdateDocumentCore(clientCase, document, request, ct);
        return Ok(dto);
    }

    private async Task<ActionResult<ClientCaseDocumentDto>> UploadDocumentVersionCore(
        ClientCase clientCase,
        IFormFile? file,
        string? documentType,
        string? status,
        string? documentKey,
        bool requireExistingDocument,
        CancellationToken ct)
    {
        if (file is null || file.Length <= 0)
        {
            return BadRequest(new { message = "Выберите файл для загрузки." });
        }

        if (file.Length > MaxCaseDocumentBytes)
        {
            return BadRequest(new { message = "Файл не должен превышать 25 МБ." });
        }

        var normalizedDocumentKey = NormalizeDocumentKey(documentKey);
        if (documentKey is not null && normalizedDocumentKey is null)
        {
            return BadRequest(new { message = "Некорректный ключ документа." });
        }

        var isNewDocument = normalizedDocumentKey is null;
        if (isNewDocument)
        {
            if (requireExistingDocument)
            {
                return BadRequest(new { message = "Документ для новой версии не найден." });
            }

            normalizedDocumentKey = Guid.NewGuid().ToString("N");
        }

        var latestVersion = isNewDocument
            ? 0
            : await _db.ClientCaseDocuments
                .Where(item => item.ClientCaseId == clientCase.Id && item.DocumentKey == normalizedDocumentKey)
                .MaxAsync(item => (int?)item.Version, ct) ?? 0;

        if (!isNewDocument && latestVersion == 0)
        {
            return BadRequest(new { message = "Документ для новой версии не найден." });
        }

        var version = latestVersion + 1;
        var safeFileName = SanitizeFileName(file.FileName);
        var storedFileName = BuildStoredFileName(normalizedDocumentKey!, version, safeFileName);
        var relativePath = _caseDocumentStorage.BuildRelativePath(clientCase.Id, storedFileName);

        try
        {
            await using var stream = file.OpenReadStream();
            await _caseDocumentStorage.SaveAsync(relativePath, stream, ct);

            var document = new ClientCaseDocument
            {
                ClientCaseId = clientCase.Id,
                DocumentKey = normalizedDocumentKey!,
                Version = version,
                DocumentType = NormalizeRequired(documentType, "Документ"),
                Status = NormalizeRequired(status, "Подготовлен"),
                OriginalFileName = safeFileName,
                StoredFileName = storedFileName,
                StoragePath = relativePath,
                ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
                SizeBytes = file.Length,
                UploadedByUserId = User.GetUserId(),
                UploadedByName = User.GetDisplayName(),
                UploadedAt = DateTime.UtcNow,
            };

            _db.ClientCaseDocuments.Add(document);
            var eventPrefix = isNewDocument
                ? $"Документ «{document.OriginalFileName}» загружен"
                : $"Документ «{document.OriginalFileName}»: загружена новая версия";
            AppendCaseEvent(
                clientCase,
                $"{eventPrefix}: тип «{document.DocumentType}», статус «{document.Status}», версия {document.Version}. Добавил: {User.GetDisplayName()}.");
            await _db.SaveChangesAsync(ct);
            AddCaseDocumentAuditEvent(
                clientCase,
                document,
                isNewDocument ? "Created" : "VersionCreated",
                isNewDocument
                    ? $"Документ «{document.OriginalFileName}» загружен."
                    : $"Загружена новая версия документа «{document.OriginalFileName}».");
            await _db.SaveChangesAsync(ct);

            return Created($"/api/case-documents/{document.Id}", MapDocument(document, true));
        }
        catch
        {
            _caseDocumentStorage.DeleteIfExists(relativePath);
            throw;
        }
    }

    private async Task<ClientCaseDocument> SaveGeneratedCaseDocumentAsync(
        ClientCase clientCase,
        string documentKey,
        string documentType,
        string status,
        string fileName,
        string html,
        string auditAction,
        string auditDescription,
        string caseEventText,
        CancellationToken ct)
    {
        var latestVersion = await _db.ClientCaseDocuments
            .Where(item => item.ClientCaseId == clientCase.Id && item.DocumentKey == documentKey)
            .MaxAsync(item => (int?)item.Version, ct) ?? 0;
        var version = latestVersion + 1;
        var safeFileName = SanitizeFileName(fileName);
        var storedFileName = BuildStoredFileName(documentKey, version, safeFileName);
        var relativePath = _caseDocumentStorage.BuildRelativePath(clientCase.Id, storedFileName);
        var content = Encoding.UTF8.GetBytes(html);

        try
        {
            await _caseDocumentStorage.SaveAsync(relativePath, content, ct);

            var document = new ClientCaseDocument
            {
                ClientCaseId = clientCase.Id,
                DocumentKey = documentKey,
                Version = version,
                DocumentType = documentType,
                Status = status,
                OriginalFileName = safeFileName,
                StoredFileName = storedFileName,
                StoragePath = relativePath,
                ContentType = "text/html; charset=utf-8",
                SizeBytes = content.LongLength,
                UploadedByUserId = User.GetUserId(),
                UploadedByName = User.GetDisplayName(),
                UploadedAt = DateTime.UtcNow,
            };

            _db.ClientCaseDocuments.Add(document);
            AppendCaseEvent(clientCase, caseEventText);
            await _db.SaveChangesAsync(ct);
            AddCaseDocumentAuditEvent(clientCase, document, auditAction, auditDescription);
            await _db.SaveChangesAsync(ct);

            return document;
        }
        catch
        {
            _caseDocumentStorage.DeleteIfExists(relativePath);
            throw;
        }
    }

    private async Task<ClientCaseDocumentDto> UpdateDocumentCore(
        ClientCase clientCase,
        ClientCaseDocument document,
        UpdateCaseDocumentRequest? request,
        CancellationToken ct)
    {
        var changes = new List<string>();
        var nextType = NormalizeOptional(request?.DocumentType);
        var nextStatus = NormalizeOptional(request?.Status);

        if (nextType is not null && !string.Equals(document.DocumentType, nextType, StringComparison.Ordinal))
        {
            changes.Add($"тип «{document.DocumentType}» → «{nextType}»");
            document.DocumentType = nextType;
        }

        if (nextStatus is not null && !string.Equals(document.Status, nextStatus, StringComparison.Ordinal))
        {
            changes.Add($"статус «{document.Status}» → «{nextStatus}»");
            document.Status = nextStatus;
        }

        if (changes.Count > 0)
        {
            AppendCaseEvent(
                clientCase,
                $"Документ «{document.OriginalFileName}»: {string.Join(", ", changes)}. Изменил: {User.GetDisplayName()}.");
            AddCaseDocumentAuditEvent(
                clientCase,
                document,
                "Updated",
                $"Документ «{document.OriginalFileName}»: {string.Join(", ", changes)}.");
            await _db.SaveChangesAsync(ct);
        }

        var latestVersion = await _db.ClientCaseDocuments
            .Where(item => item.ClientCaseId == clientCase.Id && item.DocumentKey == document.DocumentKey)
            .MaxAsync(item => (int?)item.Version, ct) ?? document.Version;

        return MapDocument(document, document.Version == latestVersion);
    }

    [HttpDelete("{id:int}/documents/{documentId:int}")]
    public async Task<IActionResult> DeleteDocument(int id, int documentId, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanDelete, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var clientCase = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var document = await _db.ClientCaseDocuments
            .FirstOrDefaultAsync(item => item.Id == documentId && item.ClientCaseId == id, ct);
        if (document is null)
        {
            return NotFound();
        }

        var storagePath = document.StoragePath;
        _db.ClientCaseDocuments.Remove(document);
        AppendCaseEvent(
            clientCase,
            $"Документ «{document.OriginalFileName}» удален. Удалил: {User.GetDisplayName()}.");
        AddCaseDocumentAuditEvent(clientCase, document, "Deleted", $"Документ «{document.OriginalFileName}» удален.");
        await _db.SaveChangesAsync(ct);
        _caseDocumentStorage.DeleteIfExists(storagePath);

        return NoContent();
    }

    private void AddCaseDocumentAuditEvent(
        ClientCase clientCase,
        ClientCaseDocument document,
        string action,
        string description)
    {
        _db.ObjectAuditEvents.Add(new ObjectAuditEvent
        {
            ObjectType = "client-case-document",
            ObjectId = document.Id,
            Action = action,
            Description = description,
            ActorUserId = User.GetUserId(),
            ActorUserName = User.GetDisplayName(),
            MetadataJson = JsonSerializer.Serialize(new
            {
                clientCaseId = clientCase.Id,
                caseTitle = clientCase.Title,
                documentKey = document.DocumentKey,
                version = document.Version,
                documentType = document.DocumentType,
                status = document.Status,
                fileName = document.OriginalFileName,
            }, CaseDocumentTemplateJsonOptions),
            CreatedAt = DateTime.UtcNow,
        });
    }

    private async Task<IReadOnlyList<CaseCalendarEventDto>> QueryCaseCalendarEventsAsync(
        DataVisibilityAccess visibility,
        DateTime fromDate,
        DateTime toDate,
        int? clientCaseId,
        int? responsibleUserId,
        bool includeTasks,
        CancellationToken ct)
    {
        var toExclusive = toDate.Date.AddDays(1);
        var caseQuery = _db.ClientCases
            .AsNoTracking()
            .ApplyClientCaseVisibility(visibility)
            .Include(item => item.Client)
            .Include(item => item.LegalEntity)
            .Include(item => item.ResponsibleUser)
            .Include(item => item.CourtEvents)
            .Include(item => item.ProceduralDeadlines)
            .AsQueryable();

        if (clientCaseId.HasValue)
        {
            caseQuery = caseQuery.Where(item => item.Id == clientCaseId.Value);
        }

        if (responsibleUserId.HasValue)
        {
            caseQuery = caseQuery.Where(item => item.ResponsibleUserId == responsibleUserId.Value);
        }

        caseQuery = caseQuery.Where(item =>
            (item.DueDate.HasValue && item.DueDate.Value >= fromDate && item.DueDate.Value < toExclusive) ||
            (item.NextHearingAt.HasValue && item.NextHearingAt.Value >= fromDate && item.NextHearingAt.Value < toExclusive) ||
            (item.ClaimFiledAt.HasValue && item.ClaimFiledAt.Value >= fromDate && item.ClaimFiledAt.Value < toExclusive) ||
            (item.CourtDecisionAt.HasValue && item.CourtDecisionAt.Value >= fromDate && item.CourtDecisionAt.Value < toExclusive) ||
            (item.AppealDeadlineAt.HasValue && item.AppealDeadlineAt.Value >= fromDate && item.AppealDeadlineAt.Value < toExclusive) ||
            (item.CassationDeadlineAt.HasValue && item.CassationDeadlineAt.Value >= fromDate && item.CassationDeadlineAt.Value < toExclusive) ||
            (item.MotionDeadlineAt.HasValue && item.MotionDeadlineAt.Value >= fromDate && item.MotionDeadlineAt.Value < toExclusive) ||
            (item.ObjectionDeadlineAt.HasValue && item.ObjectionDeadlineAt.Value >= fromDate && item.ObjectionDeadlineAt.Value < toExclusive) ||
            _db.ClientCaseCourtEvents.Any(courtEvent =>
                courtEvent.ClientCaseId == item.Id &&
                courtEvent.MovementStatus != "cancelled" &&
                courtEvent.ScheduledAt >= fromDate &&
                courtEvent.ScheduledAt < toExclusive) ||
            _db.ClientCaseProceduralDeadlines.Any(deadline =>
                deadline.ClientCaseId == item.Id &&
                deadline.Status != "closed" &&
                deadline.Status != "cancelled" &&
                deadline.DueAt >= fromDate &&
                deadline.DueAt < toExclusive) ||
            (includeTasks && _db.TaskJournalItems.Any(task =>
                task.ClientCaseId == item.Id &&
                task.DueAt >= fromDate &&
                task.DueAt < toExclusive &&
                task.Status != "done" &&
                task.Status != "cancelled")));

        var cases = await caseQuery
            .OrderBy(item => item.NextHearingAt ?? item.DueDate ?? item.AppealDeadlineAt ?? item.MotionDeadlineAt ?? item.ObjectionDeadlineAt ?? item.CourtDecisionAt ?? item.StartDate ?? DateTime.MaxValue)
            .ThenBy(item => item.Id)
            .Take(1000)
            .ToListAsync(ct);

        var events = new List<CaseCalendarEventDto>();
        foreach (var clientCase in cases)
        {
            var separateDeadlineTypes = clientCase.ProceduralDeadlines
                .Where(item => item.Status != "closed" && item.Status != "cancelled")
                .Select(item => item.DeadlineType)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            AddCaseCalendarDateEvents(events, clientCase, separateDeadlineTypes, fromDate, toExclusive);
            AddCaseCalendarCourtEvents(events, clientCase, fromDate, toExclusive);
            AddCaseCalendarProceduralDeadlineEvents(events, clientCase, fromDate, toExclusive);
        }

        if (includeTasks && cases.Count > 0)
        {
            var visibleCaseIds = cases.Select(item => item.Id).ToArray();
            var tasks = await _db.TaskJournalItems
                .AsNoTracking()
                .Where(item => item.ClientCaseId.HasValue && visibleCaseIds.Contains(item.ClientCaseId.Value))
                .Where(item => item.DueAt >= fromDate && item.DueAt < toExclusive)
                .Where(item => item.Status != "done" && item.Status != "cancelled")
                .OrderBy(item => item.DueAt)
                .ThenBy(item => item.Id)
                .Take(2000)
                .ToListAsync(ct);
            var casesById = cases.ToDictionary(item => item.Id);
            foreach (var task in tasks)
            {
                if (task.ClientCaseId.HasValue && casesById.TryGetValue(task.ClientCaseId.Value, out var clientCase))
                {
                    events.Add(MapTaskCalendarEvent(clientCase, task));
                }
            }
        }

        return events
            .OrderBy(item => item.StartAt)
            .ThenBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Title, StringComparer.CurrentCulture)
            .ToList();
    }

    private static CaseCalendarSummaryDto BuildCaseCalendarSummary(
        IReadOnlyCollection<CaseCalendarEventDto> events,
        DateTime fromDate,
        DateTime toDate)
    {
        return new CaseCalendarSummaryDto
        {
            From = fromDate,
            To = toDate,
            TotalEventCount = events.Count,
            OverdueCount = events.Count(static item => item.IsOverdue),
            WarningCount = events.Count(static item => item.IsWarning),
            CourtEventCount = events.Count(static item => item.Kind == "court"),
            ProceduralDeadlineCount = events.Count(static item => item.Kind == "procedural-deadline"),
            CaseDeadlineCount = events.Count(static item => item.Kind == "case-deadline"),
            TaskCount = events.Count(static item => item.Kind == "task"),
            NearestEvent = events
                .Where(static item => item.StartAt >= DateTime.UtcNow.Date)
                .OrderBy(static item => item.StartAt)
                .FirstOrDefault()
                ?? events.OrderByDescending(static item => item.StartAt).FirstOrDefault(),
        };
    }

    private static void AddCaseCalendarDateEvents(
        ICollection<CaseCalendarEventDto> events,
        ClientCase clientCase,
        IReadOnlySet<string> separateDeadlineTypes,
        DateTime fromDate,
        DateTime toExclusive)
    {
        AddCaseCalendarEvent(events, clientCase, "case-deadline", "due", "Крайний срок по делу", clientCase.DueDate, true, fromDate, toExclusive);
        AddCaseCalendarEvent(events, clientCase, "court", "hearing", "Судебное заседание", clientCase.NextHearingAt, false, fromDate, toExclusive);
        AddCaseCalendarEvent(events, clientCase, "court", "claim", "Подача иска", clientCase.ClaimFiledAt, true, fromDate, toExclusive);
        AddCaseCalendarEvent(events, clientCase, "court", "decision", "Судебное решение", clientCase.CourtDecisionAt, true, fromDate, toExclusive);
        AddLegacyProceduralCalendarEvent(events, clientCase, separateDeadlineTypes, "appeal", "Срок апелляции", clientCase.AppealDeadlineAt, fromDate, toExclusive);
        AddLegacyProceduralCalendarEvent(events, clientCase, separateDeadlineTypes, "cassation", "Срок кассации", clientCase.CassationDeadlineAt, fromDate, toExclusive);
        AddLegacyProceduralCalendarEvent(events, clientCase, separateDeadlineTypes, "motion", "Срок подачи ходатайства", clientCase.MotionDeadlineAt, fromDate, toExclusive);
        AddLegacyProceduralCalendarEvent(events, clientCase, separateDeadlineTypes, "objection", "Срок подачи возражений", clientCase.ObjectionDeadlineAt, fromDate, toExclusive);
    }

    private static void AddLegacyProceduralCalendarEvent(
        ICollection<CaseCalendarEventDto> events,
        ClientCase clientCase,
        IReadOnlySet<string> separateDeadlineTypes,
        string sourceType,
        string title,
        DateTime? startAt,
        DateTime fromDate,
        DateTime toExclusive)
    {
        if (separateDeadlineTypes.Contains(sourceType))
        {
            return;
        }

        AddCaseCalendarEvent(events, clientCase, "procedural-deadline", sourceType, title, startAt, true, fromDate, toExclusive);
    }

    private static void AddCaseCalendarProceduralDeadlineEvents(
        ICollection<CaseCalendarEventDto> events,
        ClientCase clientCase,
        DateTime fromDate,
        DateTime toExclusive)
    {
        foreach (var deadline in clientCase.ProceduralDeadlines
            .Where(item => item.Status != "closed" && item.Status != "cancelled")
            .Where(item => item.DueAt >= fromDate && item.DueAt < toExclusive)
            .OrderBy(item => item.DueAt)
            .ThenBy(item => item.Id))
        {
            var normalizedStart = deadline.AllDay ? deadline.DueAt.Date : deadline.DueAt;
            events.Add(new CaseCalendarEventDto
            {
                Id = $"procedural-deadline:{deadline.Id}",
                Kind = "procedural-deadline",
                SourceType = deadline.DeadlineType,
                SourceId = deadline.Id,
                Title = deadline.Title,
                StartAt = normalizedStart,
                EndAt = deadline.AllDay ? normalizedStart.Date.AddDays(1) : normalizedStart.AddHours(1),
                AllDay = deadline.AllDay,
                ClientCaseId = clientCase.Id,
                CaseTitle = clientCase.Title,
                ClientName = clientCase.Client?.Name,
                LegalEntityName = ResolveLegalEntityName(clientCase.LegalEntity),
                ResponsibleUserId = clientCase.ResponsibleUserId,
                ResponsibleName = ResolveCaseResponsibleName(clientCase),
                Status = deadline.Status,
                Priority = clientCase.Priority,
                DeadlineType = deadline.DeadlineType,
                IsOverdue = normalizedStart.Date < DateTime.UtcNow.Date,
                IsWarning = normalizedStart.Date >= DateTime.UtcNow.Date && normalizedStart.Date <= DateTime.UtcNow.Date.AddDays(Math.Max(0, deadline.WarningDaysBefore)),
                CourtName = NormalizeOptional(clientCase.CourtName),
                CourtCaseNumber = NormalizeOptional(clientCase.CourtCaseNumber),
            });
        }
    }

    private static void AddCaseCalendarCourtEvents(
        ICollection<CaseCalendarEventDto> events,
        ClientCase clientCase,
        DateTime fromDate,
        DateTime toExclusive)
    {
        foreach (var courtEvent in clientCase.CourtEvents
            .Where(item => item.MovementStatus != "cancelled")
            .Where(item => item.ScheduledAt >= fromDate && item.ScheduledAt < toExclusive)
            .OrderBy(item => item.ScheduledAt)
            .ThenBy(item => item.Id))
        {
            var normalizedStart = courtEvent.AllDay ? courtEvent.ScheduledAt.Date : courtEvent.ScheduledAt;
            events.Add(new CaseCalendarEventDto
            {
                Id = $"court-event:{courtEvent.Id}",
                Kind = "court",
                SourceType = courtEvent.EventType,
                SourceId = courtEvent.Id,
                Title = courtEvent.Title,
                StartAt = normalizedStart,
                EndAt = courtEvent.AllDay ? normalizedStart.Date.AddDays(1) : normalizedStart.AddHours(1),
                AllDay = courtEvent.AllDay,
                ClientCaseId = clientCase.Id,
                CaseTitle = clientCase.Title,
                ClientName = clientCase.Client?.Name,
                LegalEntityName = ResolveLegalEntityName(clientCase.LegalEntity),
                ResponsibleUserId = clientCase.ResponsibleUserId,
                ResponsibleName = ResolveCaseResponsibleName(clientCase),
                Status = courtEvent.MovementStatus,
                Priority = clientCase.Priority,
                DeadlineType = courtEvent.EventType,
                IsOverdue = normalizedStart.Date < DateTime.UtcNow.Date && courtEvent.MovementStatus is not "held" and not "completed",
                IsWarning = normalizedStart.Date >= DateTime.UtcNow.Date && normalizedStart.Date <= DateTime.UtcNow.Date.AddDays(7),
                CourtName = NormalizeOptional(courtEvent.CourtName) ?? NormalizeOptional(clientCase.CourtName),
                CourtCaseNumber = NormalizeOptional(courtEvent.CourtCaseNumber) ?? NormalizeOptional(clientCase.CourtCaseNumber),
            });
        }
    }

    private static void AddCaseCalendarEvent(
        ICollection<CaseCalendarEventDto> events,
        ClientCase clientCase,
        string kind,
        string sourceType,
        string title,
        DateTime? startAt,
        bool allDay,
        DateTime fromDate,
        DateTime toExclusive)
    {
        if (!startAt.HasValue || startAt.Value < fromDate || startAt.Value >= toExclusive)
        {
            return;
        }

        var normalizedStart = allDay ? startAt.Value.Date : startAt.Value;
        events.Add(new CaseCalendarEventDto
        {
            Id = $"{kind}:{sourceType}:{clientCase.Id}",
            Kind = kind,
            SourceType = sourceType,
            Title = title,
            StartAt = normalizedStart,
            EndAt = allDay ? normalizedStart.Date.AddDays(1) : normalizedStart.AddHours(1),
            AllDay = allDay,
            ClientCaseId = clientCase.Id,
            CaseTitle = clientCase.Title,
            ClientName = clientCase.Client?.Name,
            LegalEntityName = ResolveLegalEntityName(clientCase.LegalEntity),
            ResponsibleUserId = clientCase.ResponsibleUserId,
            ResponsibleName = ResolveCaseResponsibleName(clientCase),
            Status = clientCase.Status.ToString(),
            Priority = clientCase.Priority,
            DeadlineType = sourceType,
            IsOverdue = kind.Contains("deadline", StringComparison.OrdinalIgnoreCase) && normalizedStart.Date < DateTime.UtcNow.Date,
            IsWarning = kind.Contains("deadline", StringComparison.OrdinalIgnoreCase) && normalizedStart.Date >= DateTime.UtcNow.Date && normalizedStart.Date <= DateTime.UtcNow.Date.AddDays(7),
            CourtName = NormalizeOptional(clientCase.CourtName),
            CourtCaseNumber = NormalizeOptional(clientCase.CourtCaseNumber),
        });
    }

    private static CaseCalendarEventDto MapTaskCalendarEvent(ClientCase clientCase, TaskJournalItem task)
    {
        var durationMinutes = task.PlannedDurationMinutes > 0 ? task.PlannedDurationMinutes : 60;
        return new CaseCalendarEventDto
        {
            Id = $"task:{task.Id}",
            Kind = "task",
            SourceType = task.Type,
            SourceId = task.Id,
            Title = task.Title,
            StartAt = task.DueAt,
            EndAt = task.DueAt.AddMinutes(durationMinutes),
            AllDay = false,
            ClientCaseId = clientCase.Id,
            CaseTitle = clientCase.Title,
            ClientName = clientCase.Client?.Name,
            LegalEntityName = ResolveLegalEntityName(clientCase.LegalEntity),
            ResponsibleUserId = task.AssigneeUserId ?? clientCase.ResponsibleUserId,
            ResponsibleName = string.IsNullOrWhiteSpace(task.AssigneeName) ? ResolveCaseResponsibleName(clientCase) : task.AssigneeName,
            Status = task.Status,
            Priority = task.Priority,
            DeadlineType = null,
            IsOverdue = task.DueAt < DateTime.UtcNow
                && !string.Equals(task.Status, "done", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(task.Status, "cancelled", StringComparison.OrdinalIgnoreCase),
            IsWarning = task.DueAt >= DateTime.UtcNow && task.DueAt <= DateTime.UtcNow.AddDays(7),
            CourtName = NormalizeOptional(clientCase.CourtName),
            CourtCaseNumber = NormalizeOptional(clientCase.CourtCaseNumber),
        };
    }

    private static string ResolveCaseResponsibleName(ClientCase clientCase)
        => !string.IsNullOrWhiteSpace(clientCase.ResponsibleUser?.FullName)
            ? clientCase.ResponsibleUser.FullName
            : clientCase.ResponsibleUser?.Email ?? string.Empty;

    private static string? ResolveLegalEntityName(LegalEntity? legalEntity)
        => legalEntity is null
            ? null
            : NormalizeOptional(legalEntity.ShortName) ?? NormalizeOptional(legalEntity.FullName);

    [HttpPost("{id:int}/activity")]
    public async Task<ActionResult<ClientCaseActivityDto>> MarkActivity(
        int id,
        [FromBody] MarkClientCaseActivityRequest? request,
        CancellationToken ct)
    {
        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var clientCase = await _db.ClientCases
            .Include(item => item.Client)
            .Include(item => item.LegalEntity)
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var now = DateTime.UtcNow;
        var userId = User.GetUserId();
        var displayName = User.GetDisplayName();
        var responsibleUserId = clientCase.ResponsibleUserId ?? clientCase.Client?.ResponsibleUserId ?? clientCase.LegalEntity?.ResponsibleUserId;
        var resetsInactivityTimer = !responsibleUserId.HasValue || responsibleUserId == userId;
        if (resetsInactivityTimer)
        {
            clientCase.LastViewedAt = now;
            clientCase.LastViewedByUserId = userId;
            clientCase.LastViewedByUserName = displayName;
        }

        if (request?.Confirmed == true)
        {
            clientCase.ActivityConfirmedAt = now;
            clientCase.ActivityConfirmedByUserId = userId;
            clientCase.ActivityConfirmedByUserName = displayName;
            AppendCaseEvent(clientCase, $"Активность по делу подтверждена. Подтвердил: {displayName}.");
        }

        await _db.SaveChangesAsync(ct);

        return Ok(new ClientCaseActivityDto
        {
            LastViewedAt = clientCase.LastViewedAt,
            LastViewedByUserId = clientCase.LastViewedByUserId,
            LastViewedByUserName = clientCase.LastViewedByUserName,
            ActivityConfirmedAt = clientCase.ActivityConfirmedAt,
            ActivityConfirmedByUserId = clientCase.ActivityConfirmedByUserId,
            ActivityConfirmedByUserName = clientCase.ActivityConfirmedByUserName,
            InactivityRemindersEnabled = clientCase.InactivityRemindersEnabled,
            EventHistory = clientCase.EventHistory,
        });
    }

    [HttpPost("{id:int}/history")]
    public async Task<ActionResult<ClientCaseActivityDto>> AddHistoryEntry(
        int id,
        [FromBody] AddClientCaseHistoryEntryRequest request,
        CancellationToken ct)
    {
        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var message = NormalizeOptional(request.Text);
        if (message is null)
        {
            return BadRequest(new { message = "Введите текст комментария по делу." });
        }

        if (message.Length > 2000)
        {
            return BadRequest(new { message = "Комментарий по делу не должен превышать 2000 символов." });
        }

        var clientCase = await _db.ClientCases
            .Include(item => item.Client)
            .Include(item => item.LegalEntity)
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var displayName = User.GetDisplayName();
        AppendCaseEvent(clientCase, $"Комментарий по делу: {message}. Автор: {displayName}.");

        await _db.SaveChangesAsync(ct);

        return Ok(new ClientCaseActivityDto
        {
            LastViewedAt = clientCase.LastViewedAt,
            LastViewedByUserId = clientCase.LastViewedByUserId,
            LastViewedByUserName = clientCase.LastViewedByUserName,
            ActivityConfirmedAt = clientCase.ActivityConfirmedAt,
            ActivityConfirmedByUserId = clientCase.ActivityConfirmedByUserId,
            ActivityConfirmedByUserName = clientCase.ActivityConfirmedByUserName,
            InactivityRemindersEnabled = clientCase.InactivityRemindersEnabled,
            EventHistory = clientCase.EventHistory,
        });
    }

    [HttpPost("{id:int}/conversation")]
    public async Task<ActionResult<IReadOnlyList<ClientCaseConversationEntryDto>>> AddConversationEntry(
        int id,
        [FromBody] AddClientCaseConversationEntryRequest request,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var message = NormalizeOptional(request.Text);
        if (message is null)
        {
            return BadRequest(new { message = "Введите текст сообщения по делу." });
        }

        message = NormalizeHistoryText(message);
        if (message.Length > 2000)
        {
            return BadRequest(new { message = "Сообщение по делу не должно превышать 2000 символов." });
        }

        var clientCase = await _db.ClientCases
            .Include(item => item.Client)
            .Include(item => item.LegalEntity)
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var displayName = User.GetDisplayName();
        AppendCaseEvent(clientCase, $"{CaseConversationPrefix} {message}. {CaseHistoryAuthorMarker} {displayName}.");

        await _db.SaveChangesAsync(ct);

        return Ok(MapCaseConversation(clientCase.Id, clientCase.EventHistory));
    }

    [HttpGet("{id:int}/related-parties")]
    public async Task<ActionResult<IReadOnlyList<ClientCaseRelatedPartyDto>>> GetRelatedParties(
        int id,
        [FromQuery] bool includeArchived,
        CancellationToken ct)
    {
        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var exists = await _db.ClientCases
            .AsNoTracking()
            .ApplyClientCaseVisibility(visibility)
            .AnyAsync(item => item.Id == id, ct);
        if (!exists)
        {
            return NotFound();
        }

        var query = _db.ClientCaseRelatedParties
            .AsNoTracking()
            .Where(item => item.ClientCaseId == id);
        if (!includeArchived)
        {
            query = query.Where(item => !item.IsArchived);
        }

        var items = await query
            .OrderBy(item => item.IsArchived)
            .ThenBy(item => item.SourceType)
            .ThenBy(item => item.DisplayName)
            .ThenBy(item => item.Id)
            .ToListAsync(ct);

        return Ok(items.Select(MapRelatedParty).ToList());
    }

    [HttpPost("{id:int}/related-parties")]
    public async Task<ActionResult<ClientCaseRelatedPartyDto>> AddRelatedParty(
        int id,
        [FromBody] UpsertClientCaseRelatedPartyRequest request,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var clientCase = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var sourceType = NormalizeRelatedPartySourceType(request.SourceType);
        if (sourceType is null || request.SourceId <= 0)
        {
            return BadRequest(new { message = "Выберите связанное лицо или организацию из справочника." });
        }

        var role = NormalizeRequired(request.Role, "Участник");
        var notes = NormalizeOptional(request.Notes) ?? string.Empty;
        ClientCaseRelatedParty? relatedParty = null;
        LegalEntity? autoLegalEntity = null;

        if (sourceType == RelatedPartySourceClient)
        {
            var client = await _db.Clients
                .AsNoTracking()
                .Include(item => item.LegalEntity)
                .ApplyClientVisibility(visibility)
                .FirstOrDefaultAsync(item => item.Id == request.SourceId, ct);
            if (client is null)
            {
                return BadRequest(new { message = "Физическое лицо не найдено или недоступно." });
            }

            if (await _db.ClientCaseRelatedParties.AnyAsync(item =>
                    item.ClientCaseId == id &&
                    item.SourceType == RelatedPartySourceClient &&
                    !item.IsArchived &&
                    item.ClientId == client.Id, ct))
            {
                return Conflict(new { message = "Это физическое лицо уже связано с делом." });
            }

            var archivedParty = await _db.ClientCaseRelatedParties
                .FirstOrDefaultAsync(item =>
                    item.ClientCaseId == id &&
                    item.SourceType == RelatedPartySourceClient &&
                    item.IsArchived &&
                    item.ClientId == client.Id, ct);
            if (archivedParty is not null)
            {
                archivedParty.DisplayName = client.Name;
                archivedParty.Role = role;
                archivedParty.OrganizationName = client.LegalEntity?.ShortName ?? client.Company ?? string.Empty;
                archivedParty.Phone = client.Phone;
                archivedParty.Email = client.Email;
                archivedParty.Notes = notes;
                RestoreRelatedParty(archivedParty);
                _db.SetAuditReason($"Связанное лицо восстановлено в деле #{id}: {archivedParty.DisplayName} ({archivedParty.Role}).");
                AppendCaseEvent(clientCase, $"Связь с делом восстановлена: {archivedParty.DisplayName} ({archivedParty.Role}). Восстановил: {User.GetDisplayName()}.");
                await _db.SaveChangesAsync(ct);
                return Ok(MapRelatedParty(archivedParty));
            }
            relatedParty = new ClientCaseRelatedParty
            {
                ClientCaseId = id,
                SourceType = RelatedPartySourceClient,
                ClientId = client.Id,
                DisplayName = client.Name,
                Role = role,
                OrganizationName = client.LegalEntity?.ShortName ?? client.Company ?? string.Empty,
                Phone = client.Phone,
                Email = client.Email,
                Notes = notes,
                CreatedByUserId = User.GetUserId(),
                CreatedByUserName = User.GetDisplayName(),
            };
            autoLegalEntity = client.LegalEntity;
        }
        else if (sourceType == RelatedPartySourceExternalContact)
        {
            var visibleUserIds = visibility.VisibleUserIds.ToArray();
            var contactQuery = _db.ExternalContacts
                .AsNoTracking()
                .Include(item => item.LegalEntity)
                .AsQueryable();
            if (!visibility.SeesAll)
            {
                contactQuery = contactQuery.Where(item =>
                    item.ResponsibleUserId.HasValue && visibleUserIds.Contains(item.ResponsibleUserId.Value));
            }

            var contact = await contactQuery.FirstOrDefaultAsync(item => item.Id == request.SourceId, ct);
            if (contact is null)
            {
                return BadRequest(new { message = "Контакт не найден или недоступен." });
            }

            if (await _db.ClientCaseRelatedParties.AnyAsync(item =>
                    item.ClientCaseId == id &&
                    item.SourceType == RelatedPartySourceExternalContact &&
                    !item.IsArchived &&
                    item.ExternalContactId == contact.Id, ct))
            {
                return Conflict(new { message = "Этот контакт уже связан с делом." });
            }

            var archivedContactParty = await _db.ClientCaseRelatedParties
                .FirstOrDefaultAsync(item =>
                    item.ClientCaseId == id &&
                    item.SourceType == RelatedPartySourceExternalContact &&
                    item.IsArchived &&
                    item.ExternalContactId == contact.Id, ct);
            if (archivedContactParty is not null)
            {
                archivedContactParty.DisplayName = contact.FullName;
                archivedContactParty.Role = role;
                archivedContactParty.OrganizationName = contact.LegalEntity?.ShortName ?? string.Empty;
                archivedContactParty.Phone = contact.Phone ?? string.Empty;
                archivedContactParty.Email = contact.Email ?? string.Empty;
                archivedContactParty.Notes = notes;
                RestoreRelatedParty(archivedContactParty);
                _db.SetAuditReason($"Связанное лицо восстановлено в деле #{id}: {archivedContactParty.DisplayName} ({archivedContactParty.Role}).");
                AppendCaseEvent(clientCase, $"Связь с делом восстановлена: {archivedContactParty.DisplayName} ({archivedContactParty.Role}). Восстановил: {User.GetDisplayName()}.");
                await _db.SaveChangesAsync(ct);
                return Ok(MapRelatedParty(archivedContactParty));
            }

            relatedParty = new ClientCaseRelatedParty
            {
                ClientCaseId = id,
                SourceType = RelatedPartySourceExternalContact,
                ExternalContactId = contact.Id,
                DisplayName = contact.FullName,
                Role = role,
                OrganizationName = contact.LegalEntity?.ShortName ?? string.Empty,
                Phone = contact.Phone ?? string.Empty,
                Email = contact.Email ?? string.Empty,
                Notes = notes,
                CreatedByUserId = User.GetUserId(),
                CreatedByUserName = User.GetDisplayName(),
            };
            autoLegalEntity = contact.LegalEntity;
        }
        else if (sourceType == RelatedPartySourceLegalEntity)
        {
            var legalEntity = await _db.LegalEntities
                .AsNoTracking()
                .ApplyLegalEntityVisibility(visibility)
                .FirstOrDefaultAsync(item => item.Id == request.SourceId, ct);
            if (legalEntity is null)
            {
                return BadRequest(new { message = "Организация не найдена или недоступна." });
            }

            if (await _db.ClientCaseRelatedParties.AnyAsync(item =>
                    item.ClientCaseId == id &&
                    item.SourceType == RelatedPartySourceLegalEntity &&
                    !item.IsArchived &&
                    item.LegalEntityId == legalEntity.Id, ct))
            {
                return Conflict(new { message = "Эта организация уже связана с делом." });
            }

            var archivedLegalEntityParty = await _db.ClientCaseRelatedParties
                .FirstOrDefaultAsync(item =>
                    item.ClientCaseId == id &&
                    item.SourceType == RelatedPartySourceLegalEntity &&
                    item.IsArchived &&
                    item.LegalEntityId == legalEntity.Id, ct);
            if (archivedLegalEntityParty is not null)
            {
                archivedLegalEntityParty.DisplayName = !string.IsNullOrWhiteSpace(legalEntity.ShortName) ? legalEntity.ShortName : legalEntity.FullName ?? $"Юрлицо #{legalEntity.Id}";
                archivedLegalEntityParty.Role = role;
                archivedLegalEntityParty.OrganizationName = legalEntity.FullName ?? string.Empty;
                archivedLegalEntityParty.Phone = legalEntity.Phone ?? string.Empty;
                archivedLegalEntityParty.Email = legalEntity.Email ?? string.Empty;
                archivedLegalEntityParty.Notes = notes;
                RestoreRelatedParty(archivedLegalEntityParty);
                _db.SetAuditReason($"Связанное лицо восстановлено в деле #{id}: {archivedLegalEntityParty.DisplayName} ({archivedLegalEntityParty.Role}).");
                AppendCaseEvent(clientCase, $"Связь с делом восстановлена: {archivedLegalEntityParty.DisplayName} ({archivedLegalEntityParty.Role}). Восстановил: {User.GetDisplayName()}.");
                await _db.SaveChangesAsync(ct);
                return Ok(MapRelatedParty(archivedLegalEntityParty));
            }

            relatedParty = new ClientCaseRelatedParty
            {
                ClientCaseId = id,
                SourceType = RelatedPartySourceLegalEntity,
                LegalEntityId = legalEntity.Id,
                DisplayName = !string.IsNullOrWhiteSpace(legalEntity.ShortName) ? legalEntity.ShortName : legalEntity.FullName ?? $"Юрлицо #{legalEntity.Id}",
                Role = role,
                OrganizationName = legalEntity.FullName ?? string.Empty,
                Phone = legalEntity.Phone ?? string.Empty,
                Email = legalEntity.Email ?? string.Empty,
                Notes = notes,
                CreatedByUserId = User.GetUserId(),
                CreatedByUserName = User.GetDisplayName(),
            };
        }

        if (relatedParty is null)
        {
            return BadRequest(new { message = "Не удалось определить тип связи." });
        }

        _db.ClientCaseRelatedParties.Add(relatedParty);
        _db.SetAuditReason($"Связанное лицо добавлено к делу #{id}: {relatedParty.DisplayName} ({relatedParty.Role}).");
        AppendCaseEvent(clientCase, $"Связь с делом добавлена: {relatedParty.DisplayName} ({relatedParty.Role}). Добавил: {User.GetDisplayName()}.");
        var autoRelatedParty = await BuildAutoLegalEntityRelatedPartyAsync(
            id,
            clientCase.LegalEntityId,
            autoLegalEntity,
            visibility,
            User.GetUserId(),
            User.GetDisplayName(),
            ct);
        if (autoRelatedParty is not null)
        {
            _db.ClientCaseRelatedParties.Add(autoRelatedParty);
            AppendCaseEvent(
                clientCase,
                $"Связь с организацией участника добавлена автоматически: {autoRelatedParty.DisplayName}. Добавил: {User.GetDisplayName()}.");
        }

        await _db.SaveChangesAsync(ct);

        return Created($"/api/v2/cases/{id}/related-parties/{relatedParty.Id}", MapRelatedParty(relatedParty));
    }

    [HttpDelete("{id:int}/related-parties/{partyId:int}")]
    public async Task<IActionResult> DeleteRelatedParty(int id, int partyId, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var clientCase = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var relatedParty = await _db.ClientCaseRelatedParties
            .FirstOrDefaultAsync(item => item.Id == partyId && item.ClientCaseId == id, ct);
        if (relatedParty is null)
        {
            return NotFound();
        }

        if (relatedParty.IsArchived)
        {
            return NoContent();
        }

        relatedParty.IsArchived = true;
        relatedParty.ArchivedAt = DateTime.UtcNow;
        relatedParty.ArchivedByUserId = User.GetUserId();
        relatedParty.ArchivedByUserName = User.GetDisplayName();
        relatedParty.ArchiveReason = "Archived from case card";
        _db.SetAuditReason($"Связанное лицо архивировано в деле #{id}: {relatedParty.DisplayName}.");
        AppendCaseEvent(clientCase, $"Связь с делом удалена: {relatedParty.DisplayName}. Удалил: {User.GetDisplayName()}.");
        await _db.SaveChangesAsync(ct);

        return NoContent();
    }

    [HttpGet("{id:int}/work-order")]
    public async Task<ActionResult<ClientCaseWorkOrderDto>> GetWorkOrder(int id, CancellationToken ct)
    {
        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var exists = await _db.ClientCases
            .AsNoTracking()
            .ApplyClientCaseVisibility(visibility)
            .AnyAsync(item => item.Id == id, ct);
        if (!exists)
        {
            return NotFound();
        }

        var workOrder = await _db.ClientCaseWorkOrders
            .AsNoTracking()
            .Include(item => item.Items)
            .FirstOrDefaultAsync(item => item.ClientCaseId == id, ct);

        return Ok(workOrder is null
            ? ClientCaseWorkOrderDto.Empty(id)
            : MapWorkOrder(workOrder));
    }

    [HttpGet("{id:int}/work-order/print")]
    public async Task<IActionResult> PrintWorkOrder(int id, CancellationToken ct)
    {
        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var clientCase = await _db.ClientCases
            .AsNoTracking()
            .Include(item => item.Client)
            .Include(item => item.LegalEntity)
            .Include(item => item.ResponsibleUser)
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var workOrder = await _db.ClientCaseWorkOrders
            .AsNoTracking()
            .Include(item => item.Items)
            .FirstOrDefaultAsync(item => item.ClientCaseId == id, ct);
        if (workOrder is null)
        {
            return NotFound(new { message = "По делу еще нет заказ-наряда." });
        }

        return Content(BuildWorkOrderPrintHtml(clientCase, workOrder), "text/html; charset=utf-8", Encoding.UTF8);
    }

    [HttpPost("{id:int}/work-order/generate-document")]
    public async Task<ActionResult<ClientCaseDocumentDto>> GenerateWorkOrderDocument(int id, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var clientCase = await _db.ClientCases
            .Include(item => item.Client)
            .Include(item => item.LegalEntity)
            .Include(item => item.ResponsibleUser)
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var workOrder = await _db.ClientCaseWorkOrders
            .Include(item => item.Items)
            .FirstOrDefaultAsync(item => item.ClientCaseId == id, ct);
        if (workOrder is null)
        {
            return NotFound(new { message = "По делу еще нет заказ-наряда." });
        }

        var document = await SaveGeneratedCaseDocumentAsync(
            clientCase,
            BuildWorkOrderDocumentKey(workOrder.Id),
            "Заказ-наряд",
            NormalizeWorkOrderStatus(workOrder.Status) == "approved" ? "Согласован" : "Подготовлен",
            $"case-{id}-work-order.html",
            BuildWorkOrderPrintHtml(clientCase, workOrder),
            "Generated",
            $"Сформирована печатная форма заказ-наряда «{workOrder.Title}».",
            $"Заказ-наряд «{workOrder.Title}» сохранен в документы дела. Автор: {User.GetDisplayName()}.",
            ct);

        return Created($"/api/v2/cases/{id}/documents/{document.Id}", MapDocument(document, true));
    }

    [HttpPost("{id:int}/work-order")]
    public async Task<ActionResult<ClientCaseWorkOrderDto>> CreateWorkOrder(
        int id,
        [FromBody] UpsertClientCaseWorkOrderRequest? request,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var clientCase = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var existing = await _db.ClientCaseWorkOrders
            .Include(item => item.Items)
            .FirstOrDefaultAsync(item => item.ClientCaseId == id, ct);
        if (existing is not null)
        {
            return Conflict(new { message = "По делу уже есть предварительный расчет." });
        }

        var now = DateTime.UtcNow;
        var workOrder = new ClientCaseWorkOrder
        {
            ClientCaseId = id,
            Title = NormalizeRequired(request?.Title, "Предварительный расчет"),
            Status = NormalizeWorkOrderStatus(request?.Status),
            Currency = NormalizeCurrency(request?.Currency),
            Notes = Truncate(NormalizeOptional(request?.Notes) ?? string.Empty, 1000),
            CreatedAt = now,
            CreatedByUserId = User.GetUserId(),
            CreatedByUserName = User.GetDisplayName(),
        };

        _db.ClientCaseWorkOrders.Add(workOrder);
        _db.SetAuditReason($"Предварительный расчет создан в деле #{id}: {workOrder.Title}.");
        AppendCaseEvent(clientCase, $"Создан предварительный расчет «{workOrder.Title}». Автор: {User.GetDisplayName()}.");
        await _db.SaveChangesAsync(ct);

        return Created($"/api/v2/cases/{id}/work-order", MapWorkOrder(workOrder));
    }

    [HttpPatch("{id:int}/work-order")]
    public async Task<ActionResult<ClientCaseWorkOrderDto>> UpdateWorkOrder(
        int id,
        [FromBody] UpsertClientCaseWorkOrderRequest? request,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var loadResult = await LoadEditableWorkOrderAsync(id, ct);
        if (loadResult.Result is not null)
        {
            return loadResult.Result;
        }

        var (clientCase, workOrder) = loadResult.Value;
        if (IsWorkOrderLockedForEditing(workOrder))
        {
            return BadRequest(new { message = BuildWorkOrderLockedMessage(workOrder.Status) });
        }

        var changes = new List<string>();
        var nextTitle = NormalizeOptional(request?.Title);
        if (nextTitle is not null && !string.Equals(workOrder.Title, nextTitle, StringComparison.Ordinal))
        {
            workOrder.Title = nextTitle;
            changes.Add($"название: {nextTitle}");
        }

        var nextStatus = NormalizeOptional(request?.Status);
        if (nextStatus is not null)
        {
            var normalizedStatus = NormalizeWorkOrderStatus(nextStatus);
            if (IsWorkOrderApprovalManagedStatus(normalizedStatus))
            {
                return BadRequest(new { message = "Статусы согласования заказ-наряда меняются только через действия согласования и создания счета." });
            }

            if (!string.Equals(workOrder.Status, normalizedStatus, StringComparison.OrdinalIgnoreCase))
            {
                workOrder.Status = normalizedStatus;
                changes.Add($"статус: {FormatWorkOrderStatus(normalizedStatus)}");
            }
        }

        var nextCurrency = NormalizeOptional(request?.Currency);
        if (nextCurrency is not null)
        {
            var normalizedCurrency = NormalizeCurrency(nextCurrency);
            if (!string.Equals(workOrder.Currency, normalizedCurrency, StringComparison.Ordinal))
            {
                workOrder.Currency = normalizedCurrency;
                changes.Add($"валюта: {normalizedCurrency}");
            }
        }

        if (request?.Notes is not null)
        {
            workOrder.Notes = Truncate(NormalizeOptional(request.Notes) ?? string.Empty, 1000);
            changes.Add("примечание обновлено");
        }

        if (changes.Count == 0)
        {
            return Ok(MapWorkOrder(workOrder));
        }

        StampWorkOrderUpdate(workOrder);
        _db.SetAuditReason($"Предварительный расчет обновлен в деле #{id}: {string.Join("; ", changes)}.");
        AppendCaseEvent(clientCase, $"Предварительный расчет обновлен: {string.Join("; ", changes)}. Изменил: {User.GetDisplayName()}.");
        await _db.SaveChangesAsync(ct);

        return Ok(MapWorkOrder(workOrder));
    }

    [HttpDelete("{id:int}/work-order")]
    public async Task<IActionResult> DeleteWorkOrder(int id, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanDelete, ct) is { } forbidden)
        {
            return forbidden;
        }

        var loadResult = await LoadEditableWorkOrderAsync(id, ct);
        if (loadResult.Result is not null)
        {
            return loadResult.Result;
        }

        var (clientCase, workOrder) = loadResult.Value;
        if (IsWorkOrderLockedForEditing(workOrder))
        {
            return BadRequest(new { message = BuildWorkOrderLockedMessage(workOrder.Status) });
        }

        _db.ClientCaseWorkOrders.Remove(workOrder);
        _db.SetAuditReason($"Предварительный расчет удален из дела #{id}: {workOrder.Title}.");
        AppendCaseEvent(clientCase, $"Предварительный расчет «{workOrder.Title}» удален. Удалил: {User.GetDisplayName()}.");
        await _db.SaveChangesAsync(ct);

        return NoContent();
    }

    [HttpPost("{id:int}/work-order/items")]
    public async Task<ActionResult<ClientCaseWorkOrderDto>> AddWorkOrderItem(
        int id,
        [FromBody] UpsertClientCaseWorkOrderItemRequest? request,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var loadResult = await LoadEditableWorkOrderAsync(id, ct);
        if (loadResult.Result is not null)
        {
            return loadResult.Result;
        }

        var (clientCase, workOrder) = loadResult.Value;
        if (IsWorkOrderLockedForEditing(workOrder))
        {
            return BadRequest(new { message = BuildWorkOrderLockedMessage(workOrder.Status) });
        }

        var (item, itemError) = await BuildWorkOrderItemAsync(workOrder, request, ct);
        if (itemError is not null)
        {
            return BadRequest(new { message = itemError });
        }

        item ??= new ClientCaseWorkOrderItem();
        if (item.ServiceName.Length == 0)
        {
            return BadRequest(new { message = "Укажите услугу или этап работ." });
        }

        workOrder.Items.Add(item);
        RecalculateWorkOrder(workOrder);
        StampWorkOrderUpdate(workOrder);
        _db.SetAuditReason($"В предварительный расчет дела #{id} добавлена услуга: {item.ServiceName}.");
        AppendCaseEvent(clientCase, $"В предварительный расчет добавлена услуга «{item.ServiceName}» на сумму {item.TotalAmount:0.##} {workOrder.Currency}. Добавил: {User.GetDisplayName()}.");
        await _db.SaveChangesAsync(ct);

        return Ok(MapWorkOrder(workOrder));
    }

    [HttpPatch("{id:int}/work-order/items/{itemId:int}")]
    public async Task<ActionResult<ClientCaseWorkOrderDto>> UpdateWorkOrderItem(
        int id,
        int itemId,
        [FromBody] UpsertClientCaseWorkOrderItemRequest? request,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var loadResult = await LoadEditableWorkOrderAsync(id, ct);
        if (loadResult.Result is not null)
        {
            return loadResult.Result;
        }

        var (clientCase, workOrder) = loadResult.Value;
        if (IsWorkOrderLockedForEditing(workOrder))
        {
            return BadRequest(new { message = BuildWorkOrderLockedMessage(workOrder.Status) });
        }

        var item = workOrder.Items.FirstOrDefault(entry => entry.Id == itemId);
        if (item is null)
        {
            return NotFound();
        }

        var itemError = await ApplyWorkOrderItemAsync(item, request, ct);
        if (itemError is not null)
        {
            return BadRequest(new { message = itemError });
        }

        if (item.ServiceName.Length == 0)
        {
            return BadRequest(new { message = "Укажите услугу или этап работ." });
        }

        RecalculateWorkOrder(workOrder);
        StampWorkOrderUpdate(workOrder);
        _db.SetAuditReason($"Позиция предварительного расчета обновлена в деле #{id}: {item.ServiceName}.");
        AppendCaseEvent(clientCase, $"Позиция предварительного расчета «{item.ServiceName}» обновлена. Изменил: {User.GetDisplayName()}.");
        await _db.SaveChangesAsync(ct);

        return Ok(MapWorkOrder(workOrder));
    }

    [HttpDelete("{id:int}/work-order/items/{itemId:int}")]
    public async Task<ActionResult<ClientCaseWorkOrderDto>> DeleteWorkOrderItem(
        int id,
        int itemId,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var loadResult = await LoadEditableWorkOrderAsync(id, ct);
        if (loadResult.Result is not null)
        {
            return loadResult.Result;
        }

        var (clientCase, workOrder) = loadResult.Value;
        if (IsWorkOrderLockedForEditing(workOrder))
        {
            return BadRequest(new { message = BuildWorkOrderLockedMessage(workOrder.Status) });
        }

        var item = workOrder.Items.FirstOrDefault(entry => entry.Id == itemId);
        if (item is null)
        {
            return NotFound();
        }

        _db.ClientCaseWorkOrderItems.Remove(item);
        workOrder.Items.Remove(item);
        RecalculateWorkOrder(workOrder);
        StampWorkOrderUpdate(workOrder);
        _db.SetAuditReason($"Позиция предварительного расчета удалена из дела #{id}: {item.ServiceName}.");
        AppendCaseEvent(clientCase, $"Позиция предварительного расчета «{item.ServiceName}» удалена. Удалил: {User.GetDisplayName()}.");
        await _db.SaveChangesAsync(ct);

        return Ok(MapWorkOrder(workOrder));
    }

    [HttpPost("{id:int}/work-order/create-invoice")]
    public async Task<ActionResult<ClientCaseWorkOrderInvoiceDto>> CreateInvoiceFromWorkOrder(
        int id,
        [FromBody] CreateClientCaseWorkOrderInvoiceRequest? request,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var loadResult = await LoadEditableWorkOrderAsync(id, ct);
        if (loadResult.Result is not null)
        {
            return loadResult.Result;
        }

        var (clientCase, workOrder) = loadResult.Value;
        RecalculateWorkOrder(workOrder);
        if (workOrder.TotalAmount <= 0m || workOrder.Items.Count == 0)
        {
            return BadRequest(new { message = "Добавьте услуги с ненулевой стоимостью перед созданием счета." });
        }
        if (!string.Equals(NormalizeWorkOrderStatus(workOrder.Status), "approved", StringComparison.Ordinal))
        {
            return BadRequest(new { message = "Перед созданием счета согласуйте заказ-наряд." });
        }

        var invoiceDate = request?.Date?.Date ?? DateTime.UtcNow.Date;
        var dueDate = request?.DueDate?.Date ?? invoiceDate;
        var number = await BuildWorkOrderInvoiceNumberAsync(id, workOrder.Id, invoiceDate, request?.Number, ct);
        var now = DateTime.UtcNow;
        var invoice = new Invoice
        {
            Number = number,
            Date = invoiceDate,
            CreatedAt = now,
        };
        var payment = new Payment
        {
            Account = number,
            AccountDate = invoiceDate,
            Invoice = invoice,
            Date = dueDate,
            PlannedDate = dueDate,
            Amount = workOrder.TotalAmount,
            PaidAmount = 0m,
            Status = PaymentStatus.Pending,
            InvoiceWorkflowStatus = InvoiceWorkflowStatus.Draft,
            Type = PaymentType.Income,
            DocumentKind = DocumentKind.Invoice,
            Description = NormalizeOptional(request?.Description)
                ?? $"Счет по заказ-наряду «{workOrder.Title}»",
            ClientId = clientCase.ClientId,
            LegalEntityId = clientCase.LegalEntityId,
            ClientCaseId = clientCase.Id,
            ClientCaseReference = NormalizeOptional(clientCase.CaseNumber) ?? clientCase.Title,
            CreatedAt = now,
        };

        payment.InvoiceClientCases.Add(new InvoiceClientCase
        {
            ClientCaseId = clientCase.Id,
        });

        var responsibleIds = workOrder.Items
            .Where(item => item.ExecutorUserId.HasValue)
            .Select(item => item.ExecutorUserId!.Value)
            .Distinct()
            .ToList();
        foreach (var responsibleId in responsibleIds)
        {
            payment.Responsibles.Add(new PaymentResponsible
            {
                UserId = responsibleId,
            });
        }

        var sortOrder = 1;
        foreach (var item in workOrder.Items.OrderBy(item => item.SortOrder).ThenBy(item => item.Id))
        {
            payment.InvoiceLineItems.Add(new InvoiceLineItem
            {
                Name = item.ServiceName,
                Quantity = item.Quantity,
                Unit = "шт",
                Price = item.UnitPrice,
                SortOrder = sortOrder++,
                CreatedAt = now,
            });
        }

        PaymentDomainService.Normalize(payment);
        workOrder.Status = "invoice_created";
        StampWorkOrderUpdate(workOrder);
        _db.SetAuditReason($"По предварительному расчету дела #{id} создан счет №{number}.");
        AppendCaseEvent(clientCase, $"По предварительному расчету «{workOrder.Title}» создан счет №{number} на сумму {workOrder.TotalAmount:0.##} {workOrder.Currency}. Создал: {User.GetDisplayName()}.");

        _db.Payments.Add(payment);
        await _db.SaveChangesAsync(ct);

        var invoiceDocument = await SaveGeneratedCaseDocumentAsync(
            clientCase,
            BuildInvoiceDocumentKey(payment.Id),
            "Счет",
            "Черновик",
            $"case-{id}-invoice-{payment.Id}.html",
            BuildWorkOrderInvoiceDocumentHtml(clientCase, workOrder, payment, number, invoiceDate, dueDate),
            "Generated",
            $"Сформирован документ счета №{number} из заказ-наряда «{workOrder.Title}».",
            $"Счет №{number} из заказ-наряда «{workOrder.Title}» сохранен в документы дела. Автор: {User.GetDisplayName()}.",
            ct);

        return Created($"/invoices/{payment.Id}", new ClientCaseWorkOrderInvoiceDto
        {
            PaymentId = payment.Id,
            InvoiceEntityId = payment.InvoiceId ?? invoice.Id,
            Number = number,
            Date = invoiceDate,
            DueDate = dueDate,
            Amount = payment.Amount,
            DocumentId = invoiceDocument.Id,
            WorkOrder = MapWorkOrder(workOrder),
        });
    }

    [HttpPost("{id:int}/work-order/submit-approval")]
    public async Task<ActionResult<ClientCaseWorkOrderDto>> SubmitWorkOrderApproval(
        int id,
        [FromBody] ClientCaseWorkOrderApprovalRequest? request,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var loadResult = await LoadEditableWorkOrderAsync(id, ct);
        if (loadResult.Result is not null)
        {
            return loadResult.Result;
        }

        var (clientCase, workOrder) = loadResult.Value;
        RecalculateWorkOrder(workOrder);
        if (workOrder.TotalAmount <= 0m || workOrder.Items.Count == 0)
        {
            return BadRequest(new { message = "Добавьте услуги с ненулевой стоимостью перед отправкой заказ-наряда на согласование." });
        }

        var now = DateTime.UtcNow;
        workOrder.Status = "pending_approval";
        workOrder.ApprovalRequestedAt = now;
        workOrder.ApprovalRequestedByUserId = User.GetUserId();
        workOrder.ApprovalRequestedByUserName = User.GetDisplayName();
        workOrder.ApprovalDecisionAt = null;
        workOrder.ApprovalDecisionByUserId = null;
        workOrder.ApprovalDecisionByUserName = string.Empty;
        workOrder.ApprovalComment = Truncate(NormalizeOptional(request?.Comment) ?? string.Empty, 1000);
        StampWorkOrderUpdate(workOrder);
        _db.SetAuditReason($"Заказ-наряд дела #{id} отправлен на согласование.");
        AppendCaseEvent(clientCase, $"Заказ-наряд «{workOrder.Title}» отправлен на согласование. Инициатор: {User.GetDisplayName()}.{FormatOptionalComment(workOrder.ApprovalComment)}");
        await _db.SaveChangesAsync(ct);

        return Ok(MapWorkOrder(workOrder));
    }

    [HttpPost("{id:int}/work-order/approve")]
    public async Task<ActionResult<ClientCaseWorkOrderDto>> ApproveWorkOrder(
        int id,
        [FromBody] ClientCaseWorkOrderApprovalRequest? request,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var loadResult = await LoadEditableWorkOrderAsync(id, ct);
        if (loadResult.Result is not null)
        {
            return loadResult.Result;
        }

        var (clientCase, workOrder) = loadResult.Value;
        if (!string.Equals(NormalizeWorkOrderStatus(workOrder.Status), "pending_approval", StringComparison.Ordinal))
        {
            return BadRequest(new { message = "Согласовать можно только заказ-наряд в статусе «На согласовании»." });
        }

        ApplyWorkOrderApprovalDecision(workOrder, "approved", request?.Comment);
        _db.SetAuditReason($"Заказ-наряд дела #{id} согласован.");
        AppendCaseEvent(clientCase, $"Заказ-наряд «{workOrder.Title}» согласован. Согласующий: {User.GetDisplayName()}.{FormatOptionalComment(workOrder.ApprovalComment)}");
        await _db.SaveChangesAsync(ct);

        return Ok(MapWorkOrder(workOrder));
    }

    [HttpPost("{id:int}/work-order/request-revision")]
    public async Task<ActionResult<ClientCaseWorkOrderDto>> RequestWorkOrderRevision(
        int id,
        [FromBody] ClientCaseWorkOrderApprovalRequest? request,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var loadResult = await LoadEditableWorkOrderAsync(id, ct);
        if (loadResult.Result is not null)
        {
            return loadResult.Result;
        }

        var (clientCase, workOrder) = loadResult.Value;
        var currentStatus = NormalizeWorkOrderStatus(workOrder.Status);
        if (currentStatus is not "pending_approval" and not "approved")
        {
            return BadRequest(new { message = "Вернуть на доработку можно только заказ-наряд в статусе «На согласовании» или «Согласован»." });
        }

        ApplyWorkOrderApprovalDecision(workOrder, "revision_required", request?.Comment);
        _db.SetAuditReason($"Заказ-наряд дела #{id} возвращен на доработку.");
        AppendCaseEvent(clientCase, $"Заказ-наряд «{workOrder.Title}» возвращен на доработку. Согласующий: {User.GetDisplayName()}.{FormatOptionalComment(workOrder.ApprovalComment)}");
        await _db.SaveChangesAsync(ct);

        return Ok(MapWorkOrder(workOrder));
    }

    [HttpGet("{id:int}/court-sync/logs")]
    public async Task<ActionResult<IReadOnlyList<ClientCaseCourtSyncLogDto>>> GetCourtSyncLogs(int id, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var clientCase = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var logs = SplitCaseHistory(clientCase.EventHistory)
            .Where(IsCourtSyncHistoryLine)
            .Select((line, index) => MapCourtSyncLog(clientCase.Id, line, index))
            .OrderByDescending(item => item.OccurredAt ?? DateTime.MinValue)
            .ThenByDescending(item => item.Id)
            .ToList();

        return Ok(logs);
    }

    [HttpPost("{id:int}/court-sync/run")]
    public async Task<ActionResult<ClientCaseCourtSyncRunDto>> RunCourtSync(int id, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var clientCase = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var actorName = User.GetDisplayName();
        var checkedAt = DateTime.UtcNow;
        var hasCourtBinding =
            !string.IsNullOrWhiteSpace(clientCase.CourtCaseNumber)
            || !string.IsNullOrWhiteSpace(clientCase.CourtUrl);
        if (!hasCourtBinding)
        {
            const string missingBindingMessage = "Ручная проверка судебного источника не выполнена: в деле не указан номер дела в суде или ссылка на первоисточник.";
            AppendCaseEvent(clientCase, $"{missingBindingMessage} Инициатор: {actorName}.");
            await _db.SaveChangesAsync(ct);

            return Ok(new ClientCaseCourtSyncRunDto
            {
                ClientCaseId = clientCase.Id,
                Status = "binding-required",
                HasCourtBinding = false,
                Message = missingBindingMessage,
                CheckedAt = checkedAt,
                CheckedByUserId = User.GetUserId(),
                CheckedByUserName = actorName,
                CourtName = clientCase.CourtName,
                CourtCaseNumber = clientCase.CourtCaseNumber,
                CourtUrl = clientCase.CourtUrl,
            });
        }

        if (_sudrfCourtSyncService is not null
            && !string.IsNullOrWhiteSpace(clientCase.CourtUrl)
            && _sudrfCourtSyncService.CanHandle(clientCase.CourtUrl))
        {
            try
            {
                var courtData = await _sudrfCourtSyncService.FetchCaseAsync(clientCase.CourtUrl, ct, clientCase.CourtCaseNumber);
                var syncResult = await ApplySudrfCourtSyncAsync(clientCase, courtData, actorName, checkedAt, ct);
                var syncMessage = syncResult.ChangedFields.Count == 0 && syncResult.CreatedCourtEvents == 0 && syncResult.UpdatedCourtEvents == 0
                    ? $"Судебный источник проверен: новых изменений не найдено. Суд: {FormatCaseTextValue(clientCase.CourtName)}; № в суде: {FormatCaseTextValue(clientCase.CourtCaseNumber)}."
                    : $"Судебный источник обновлен: {FormatCourtSyncResult(syncResult)}";

                AppendCaseEvent(clientCase, $"{syncMessage} Инициатор: {actorName}.");
                _db.SetAuditReason($"Обновление дела #{clientCase.Id} из ГАС «Правосудие».");
                await _db.SaveChangesAsync(ct);

                return Ok(new ClientCaseCourtSyncRunDto
                {
                    ClientCaseId = clientCase.Id,
                    Status = "external-sync-completed",
                    HasCourtBinding = true,
                    Message = syncMessage,
                    CheckedAt = checkedAt,
                    CheckedByUserId = User.GetUserId(),
                    CheckedByUserName = actorName,
                    CourtName = clientCase.CourtName,
                    CourtCaseNumber = clientCase.CourtCaseNumber,
                    CourtUrl = clientCase.CourtUrl,
                    SyncedFromExternalSource = true,
                    UpdatedFields = syncResult.ChangedFields,
                    CreatedCourtEvents = syncResult.CreatedCourtEvents,
                    UpdatedCourtEvents = syncResult.UpdatedCourtEvents,
                    NextHearingAt = clientCase.NextHearingAt,
                });
            }
            catch (SudrfCourtSyncException ex)
            {
                var errorMessage = $"Проверка судебного источника не выполнена: {ex.Message} Проверьте ссылку на карточку дела суда и повторите попытку.";
                AppendCaseEvent(clientCase, $"{errorMessage} Инициатор: {actorName}.");
                await _db.SaveChangesAsync(ct);

                return Ok(new ClientCaseCourtSyncRunDto
                {
                    ClientCaseId = clientCase.Id,
                    Status = "external-sync-error",
                    HasCourtBinding = true,
                    Message = errorMessage,
                    CheckedAt = checkedAt,
                    CheckedByUserId = User.GetUserId(),
                    CheckedByUserName = actorName,
                    CourtName = clientCase.CourtName,
                    CourtCaseNumber = clientCase.CourtCaseNumber,
                    CourtUrl = clientCase.CourtUrl,
                });
            }
        }

        var status = "manual-check-recorded";
        var message = string.IsNullOrWhiteSpace(clientCase.CourtUrl)
            ? $"Ручная проверка судебного источника зафиксирована: для автоматического обновления добавьте ссылку на карточку дела суда. Суд: {FormatCaseTextValue(clientCase.CourtName)}; № в суде: {FormatCaseTextValue(clientCase.CourtCaseNumber)}."
            : $"Ручная проверка судебного источника зафиксирована: автоматическое обновление сейчас поддерживает ссылки на сайты sudrf.ru. Суд: {FormatCaseTextValue(clientCase.CourtName)}; № в суде: {FormatCaseTextValue(clientCase.CourtCaseNumber)}.";

        AppendCaseEvent(clientCase, $"{message} Инициатор: {actorName}.");
        await _db.SaveChangesAsync(ct);

        return Ok(new ClientCaseCourtSyncRunDto
        {
            ClientCaseId = clientCase.Id,
            Status = status,
            HasCourtBinding = hasCourtBinding,
            Message = message,
            CheckedAt = checkedAt,
            CheckedByUserId = User.GetUserId(),
            CheckedByUserName = actorName,
            CourtName = clientCase.CourtName,
            CourtCaseNumber = clientCase.CourtCaseNumber,
            CourtUrl = clientCase.CourtUrl,
        });
    }

    private async Task<CourtSyncApplyResult> ApplySudrfCourtSyncAsync(
        ClientCase clientCase,
        SudrfCourtCaseData courtData,
        string actorName,
        DateTime checkedAt,
        CancellationToken ct)
    {
        var actorUserId = User.GetUserId();
        var changedFields = new List<string>();
        var createdEvents = new List<ClientCaseCourtEvent>();
        var updatedCourtEvents = 0;

        UpdateString("суд", clientCase.CourtName, courtData.CourtName, value => clientCase.CourtName = value, 200);
        UpdateString("номер дела в суде", clientCase.CourtCaseNumber, courtData.CaseNumber, value => clientCase.CourtCaseNumber = value, 100);
        UpdateString("ссылка на источник", clientCase.CourtUrl, courtData.SourceUrl, value => clientCase.CourtUrl = value, 1000);
        UpdateString("судья", clientCase.JudgeName, courtData.JudgeName, value => clientCase.JudgeName = value, 200);
        UpdateString("зал/место заседания", clientCase.Courtroom, courtData.NextHearingPlace, value => clientCase.Courtroom = value, 120);
        UpdateString("истец/заявитель", clientCase.Claimant, JoinParticipants(courtData.Participants, "ИСТЕЦ", "ЗАЯВИТЕЛ"), value => clientCase.Claimant = value, 300);
        UpdateString("ответчик", clientCase.Defendant, JoinParticipants(courtData.Participants, "ОТВЕТЧИК", "ДОЛЖНИК"), value => clientCase.Defendant = value, 300);
        UpdateString("третьи лица", clientCase.ThirdParties, JoinParticipants(courtData.Participants, "ТРЕТ", "ЗАИНТЕРЕС"), value => clientCase.ThirdParties = value, 1000);
        UpdateString("представители", clientCase.Representatives, JoinParticipants(courtData.Participants, "ПРЕДСТАВИТЕЛ"), value => clientCase.Representatives = value, 1000);

        if (courtData.NextHearingAt.HasValue && clientCase.NextHearingAt != courtData.NextHearingAt)
        {
            clientCase.NextHearingAt = courtData.NextHearingAt;
            changedFields.Add("ближайшее заседание");
        }

        var hearings = courtData.MovementEvents
            .Where(static item => item.ScheduledAt.HasValue && item.Title.Contains("судебное заседание", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static item => item.ScheduledAt)
            .ToList();
        if (hearings.Count > 0)
        {
            var existingEvents = await _db.ClientCaseCourtEvents
                .Where(item => item.ClientCaseId == clientCase.Id && item.SourceType == "sudrf")
                .ToListAsync(ct);

            foreach (var hearing in hearings)
            {
                var scheduledAt = hearing.ScheduledAt!.Value;
                var title = Truncate(NormalizeOptional(hearing.Title) ?? "Судебное заседание", 200);
                var existing = existingEvents.FirstOrDefault(item =>
                    item.ScheduledAt == scheduledAt
                    && item.Title.Equals(title, StringComparison.OrdinalIgnoreCase));
                var summary = Truncate(hearing.SourceSummary, 1000);

                if (existing is null)
                {
                    var courtEvent = new ClientCaseCourtEvent
                    {
                        ClientCaseId = clientCase.Id,
                        EventType = "hearing",
                        Title = title,
                        Description = Truncate(hearing.SourceSummary, 1000),
                        ScheduledAt = scheduledAt,
                        AllDay = scheduledAt.TimeOfDay == TimeSpan.Zero,
                        CourtInstance = courtData.Instance,
                        CourtName = Truncate(NormalizeOptional(courtData.CourtName) ?? clientCase.CourtName, 200),
                        CourtCaseNumber = Truncate(NormalizeOptional(courtData.CaseNumber) ?? clientCase.CourtCaseNumber, 100),
                        Courtroom = Truncate(hearing.Place, 120),
                        JudgeName = Truncate(NormalizeOptional(courtData.JudgeName) ?? clientCase.JudgeName, 200),
                        MovementStatus = ResolveSudrfMovementStatus(hearing.Result),
                        ResultSummary = Truncate(hearing.Result, 1000),
                        SourceType = "sudrf",
                        CreatedAt = checkedAt,
                        CreatedByUserId = actorUserId,
                        CreatedByUserName = Truncate(actorName, 256),
                        UpdatedAt = checkedAt,
                        UpdatedByUserId = actorUserId,
                        UpdatedByUserName = Truncate(actorName, 256),
                    };
                    _db.ClientCaseCourtEvents.Add(courtEvent);
                    existingEvents.Add(courtEvent);
                    createdEvents.Add(courtEvent);
                    continue;
                }

                var updated = false;
                updated |= UpdateCourtEventString(existing.CourtName, courtData.CourtName, value => existing.CourtName = value, 200);
                updated |= UpdateCourtEventString(existing.CourtCaseNumber, courtData.CaseNumber, value => existing.CourtCaseNumber = value, 100);
                updated |= UpdateCourtEventString(existing.Courtroom, hearing.Place, value => existing.Courtroom = value, 120);
                updated |= UpdateCourtEventString(existing.JudgeName, courtData.JudgeName, value => existing.JudgeName = value, 200);
                updated |= UpdateCourtEventString(existing.Description, hearing.SourceSummary, value => existing.Description = value, 1000);
                updated |= UpdateCourtEventString(existing.ResultSummary, hearing.Result, value => existing.ResultSummary = value, 1000);
                var movementStatus = ResolveSudrfMovementStatus(hearing.Result);
                if (!existing.MovementStatus.Equals(movementStatus, StringComparison.OrdinalIgnoreCase))
                {
                    existing.MovementStatus = movementStatus;
                    updated = true;
                }

                if (updated)
                {
                    existing.UpdatedAt = checkedAt;
                    existing.UpdatedByUserId = actorUserId;
                    existing.UpdatedByUserName = Truncate(actorName, 256);
                    AddCourtEventHistory(existing, "updated", $"Событие обновлено из ГАС «Правосудие»: {summary}.", actorUserId, actorName);
                    updatedCourtEvents++;
                }
            }
        }

        if (createdEvents.Count > 0)
        {
            await _db.SaveChangesAsync(ct);
            foreach (var courtEvent in createdEvents)
            {
                AddCourtEventHistory(courtEvent, "created", $"Событие создано из ГАС «Правосудие»: {courtEvent.Title}.", actorUserId, actorName);
            }
        }

        return new CourtSyncApplyResult(changedFields, createdEvents.Count, updatedCourtEvents);

        void UpdateString(string label, string currentValue, string sourceValue, Action<string> setValue, int maxLength)
        {
            var normalized = NormalizeOptional(sourceValue);
            if (normalized is null)
            {
                return;
            }

            var next = Truncate(normalized, maxLength);
            if (string.Equals(currentValue, next, StringComparison.Ordinal))
            {
                return;
            }

            setValue(next);
            changedFields.Add(label);
        }

        static bool UpdateCourtEventString(string currentValue, string sourceValue, Action<string> setValue, int maxLength)
        {
            var normalized = NormalizeOptional(sourceValue);
            if (normalized is null)
            {
                return false;
            }

            var next = Truncate(normalized, maxLength);
            if (string.Equals(currentValue, next, StringComparison.Ordinal))
            {
                return false;
            }

            setValue(next);
            return true;
        }
    }

    private static string FormatCourtSyncResult(CourtSyncApplyResult result)
    {
        var parts = new List<string>();
        if (result.ChangedFields.Count > 0)
        {
            parts.Add($"обновлены поля: {string.Join(", ", result.ChangedFields.Distinct(StringComparer.OrdinalIgnoreCase))}");
        }

        if (result.CreatedCourtEvents > 0)
        {
            parts.Add($"добавлено судебных событий: {result.CreatedCourtEvents}");
        }

        if (result.UpdatedCourtEvents > 0)
        {
            parts.Add($"обновлено судебных событий: {result.UpdatedCourtEvents}");
        }

        return string.Join("; ", parts) + ".";
    }

    private static string JoinParticipants(
        IReadOnlyList<SudrfCourtParticipant> participants,
        params string[] roleMarkers)
    {
        var names = participants
            .Where(item => roleMarkers.Any(marker => item.Role.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            .Select(static item => item.Name)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return string.Join("; ", names);
    }

    private static string ResolveSudrfMovementStatus(string result)
    {
        if (result.Contains("отлож", StringComparison.OrdinalIgnoreCase))
        {
            return "postponed";
        }

        if (result.Contains("отмен", StringComparison.OrdinalIgnoreCase)
            || result.Contains("снят", StringComparison.OrdinalIgnoreCase))
        {
            return "cancelled";
        }

        if (result.Contains("рассмотр", StringComparison.OrdinalIgnoreCase)
            || result.Contains("решен", StringComparison.OrdinalIgnoreCase)
            || result.Contains("вынес", StringComparison.OrdinalIgnoreCase))
        {
            return "completed";
        }

        return string.IsNullOrWhiteSpace(result) ? "scheduled" : "held";
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ClientCase model, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanCreate, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var validation = await ValidateCaseOwnerAsync(model.ClientId, model.LegalEntityId, visibility, ct);
        if (validation is not null) return validation;
        ApplyCaseDetails(model, model);
        ApplyHiddenFromExecutorMetadata(model, model.IsHiddenFromExecutor, User.GetUserId(), User.GetDisplayName());
        _db.ClientCases.Add(model);
        await _db.SaveChangesAsync(ct);
        await ClientCaseDeadlineTaskSyncService.SyncAsync(_db, model.Id, User.GetUserId(), User.GetDisplayName(), ct);
        return Created($"/api/v2/cases/{model.Id}", model);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] ClientCase model, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var e = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (e is null) return NotFound();
        var validation = await ValidateCaseOwnerAsync(model.ClientId, model.LegalEntityId, visibility, ct);
        if (validation is not null) return validation;

        var shouldCloseLinkedTasks = !IsFinishedCaseStatus(e.Status) && IsFinishedCaseStatus(model.Status);
        e.Title = model.Title;
        e.Description = model.Description;
        e.Status = model.Status;
        e.ClientId = model.ClientId;
        e.LegalEntityId = model.LegalEntityId;
        var previousHiddenFromExecutor = e.IsHiddenFromExecutor;
        ApplyCaseDetails(e, model);
        if (previousHiddenFromExecutor != e.IsHiddenFromExecutor)
        {
            ApplyHiddenFromExecutorMetadata(e, e.IsHiddenFromExecutor, User.GetUserId(), User.GetDisplayName());
        }

        if (shouldCloseLinkedTasks)
        {
            await ClientCaseTaskSyncService.CloseActiveTasksForClosedCaseAsync(_db, e.Id, User.GetUserId(), User.GetDisplayName(), ct);
        }

        await _db.SaveChangesAsync(ct);
        await ClientCaseDeadlineTaskSyncService.SyncAsync(_db, e.Id, User.GetUserId(), User.GetDisplayName(), ct);
        return Ok(e);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanDelete, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var e = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (e is null) return NotFound();

        var payments = await _db.Payments.Where(p => p.ClientCaseId == id).ToListAsync(ct);
        foreach (var p in payments) p.ClientCaseId = null;
        await _db.SaveChangesAsync(ct);

        _db.ClientCases.Remove(e);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("bulk-update")]
    public async Task<IActionResult> BulkUpdate([FromBody] BulkUpdateCasesRequest request, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var ids = (request.Ids ?? Array.Empty<int>())
            .Where(id => id > 0)
            .Distinct()
            .Take(500)
            .ToArray();
        if (ids.Length == 0)
        {
            return BadRequest("Выберите дела для группового изменения.");
        }

        var nextStage = NormalizeOptional(request.Stage);
        var nextPriority = NormalizeOptional(request.Priority);
        if (request.Status is null
            && !request.ResponsibleUserId.HasValue
            && nextStage is null
            && nextPriority is null
            && !request.IsHiddenFromExecutor.HasValue
            && !request.InactivityRemindersEnabled.HasValue)
        {
            return BadRequest("Укажите новый статус, ответственного, стадию, приоритет, режим доступа или автоконтроль.");
        }

        User? nextResponsible = null;
        if (request.ResponsibleUserId.HasValue)
        {
            nextResponsible = await _db.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(user => user.Id == request.ResponsibleUserId.Value && user.IsActive, ct);
        }

        if (request.ResponsibleUserId.HasValue && nextResponsible is null)
        {
            return BadRequest($"Unknown responsibleUserId {request.ResponsibleUserId.Value}");
        }

        if (nextStage is not null && nextStage.Length > 160)
        {
            return BadRequest("Стадия дела слишком длинная.");
        }

        if (nextPriority is not null && !AllowedCasePriorities.Contains(nextPriority))
        {
            return BadRequest("Некорректный приоритет дела.");
        }

        var cases = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .Include(item => item.ResponsibleUser)
            .Where(item => ids.Contains(item.Id))
            .ToListAsync(ct);
        if (cases.Count == 0)
        {
            return NotFound();
        }

        var casesClosedByUpdate = new List<int>();
        var changedCasesCount = 0;
        var actorUserId = User.GetUserId();
        var actorName = User.GetDisplayName();
        foreach (var item in cases)
        {
            var changes = new List<string>();
            if (request.Status is not null)
            {
                if (!IsFinishedCaseStatus(item.Status) && IsFinishedCaseStatus(request.Status.Value))
                {
                    casesClosedByUpdate.Add(item.Id);
                }

                if (item.Status != request.Status.Value)
                {
                    changes.Add($"статус «{FormatCaseStatus(item.Status)}» → «{FormatCaseStatus(request.Status.Value)}»");
                    item.Status = request.Status.Value;
                }
            }

            if (request.ResponsibleUserId.HasValue)
            {
                if (item.ResponsibleUserId != request.ResponsibleUserId.Value)
                {
                    changes.Add($"ответственный «{FormatCaseResponsible(item.ResponsibleUser)}» → «{FormatCaseResponsible(nextResponsible)}»");
                    item.ResponsibleUserId = request.ResponsibleUserId.Value;
                }
            }

            if (nextStage is not null && !string.Equals(item.Stage, nextStage, StringComparison.Ordinal))
            {
                changes.Add($"стадия «{FormatCaseTextValue(item.Stage)}» → «{nextStage}»");
                item.Stage = nextStage;
            }

            if (nextPriority is not null && !string.Equals(item.Priority, nextPriority, StringComparison.OrdinalIgnoreCase))
            {
                changes.Add($"приоритет «{FormatCasePriority(item.Priority)}» → «{FormatCasePriority(nextPriority)}»");
                item.Priority = nextPriority;
            }

            if (request.IsHiddenFromExecutor.HasValue && item.IsHiddenFromExecutor != request.IsHiddenFromExecutor.Value)
            {
                var nextHidden = request.IsHiddenFromExecutor.Value;
                changes.Add(nextHidden ? "доступ: скрыто от исполнителя" : "доступ: открыто исполнителю");
                item.IsHiddenFromExecutor = nextHidden;
                if (nextHidden)
                {
                    ApplyHiddenFromExecutorMetadata(item, true, actorUserId, actorName);
                }
                else
                {
                    ApplyHiddenFromExecutorMetadata(item, false, actorUserId, actorName);
                }
            }

            if (request.InactivityRemindersEnabled.HasValue && item.InactivityRemindersEnabled != request.InactivityRemindersEnabled.Value)
            {
                var nextEnabled = request.InactivityRemindersEnabled.Value;
                changes.Add(nextEnabled ? "автоконтроль: включен" : "автоконтроль: выключен");
                item.InactivityRemindersEnabled = nextEnabled;
            }

            if (changes.Count > 0)
            {
                changedCasesCount++;
                AppendCaseEvent(item, $"Массовое изменение: {string.Join("; ", changes)}. Изменил: {actorName}.");
            }
        }

        if (changedCasesCount > 0)
        {
            _db.SetAuditReason(BuildBulkUpdateAuditReason(request, nextResponsible, nextStage, nextPriority, changedCasesCount));
        }

        if (casesClosedByUpdate.Count > 0)
        {
            await ClientCaseTaskSyncService.CloseActiveTasksForClosedCasesAsync(_db, casesClosedByUpdate, User.GetUserId(), User.GetDisplayName(), ct);
        }

        await _db.SaveChangesAsync(ct);
        return Ok(new { updated = cases.Count, requested = ids.Length });
    }

    private async Task<IActionResult?> ValidateCaseOwnerAsync(
        int? clientId,
        int? legalEntityId,
        DataVisibilityAccess visibility,
        CancellationToken ct)
    {
        var hasClient = clientId.HasValue && clientId.Value > 0;
        var hasLegalEntity = legalEntityId.HasValue && legalEntityId.Value > 0;
        if (hasClient == hasLegalEntity)
        {
            return BadRequest("У дела должен быть выбран ровно один владелец: клиент или юридическое лицо.");
        }

        if (hasClient && !await _db.Clients
                .AsNoTracking()
                .ApplyClientVisibility(visibility)
                .AnyAsync(client => client.Id == clientId!.Value, ct))
        {
            return BadRequest($"Unknown clientId {clientId!.Value}");
        }

        if (hasLegalEntity && !await _db.LegalEntities
                .AsNoTracking()
                .ApplyLegalEntityVisibility(visibility)
                .AnyAsync(entity => entity.Id == legalEntityId!.Value, ct))
        {
            return BadRequest($"Unknown legalEntityId {legalEntityId!.Value}");
        }

        return null;
    }

    private static void ApplyCaseDetails(ClientCase target, ClientCase source)
    {
        var previousCourtDecisionAt = target.CourtDecisionAt;
        var previousAppealDeadlineAt = target.AppealDeadlineAt;
        var previousCassationDeadlineAt = target.CassationDeadlineAt;

        target.CaseNumber = source.CaseNumber?.Trim() ?? string.Empty;
        target.CaseType = source.CaseType?.Trim() ?? string.Empty;
        target.Stage = source.Stage?.Trim() ?? string.Empty;
        target.ResponsibleUserId = source.ResponsibleUserId;
        target.StartDate = source.StartDate;
        target.DueDate = source.DueDate;
        target.FeeAmount = source.FeeAmount;
        target.Priority = string.IsNullOrWhiteSpace(source.Priority) ? "normal" : source.Priority.Trim();
        target.CourtName = source.CourtName?.Trim() ?? string.Empty;
        target.CourtCaseNumber = source.CourtCaseNumber?.Trim() ?? string.Empty;
        target.CourtUrl = source.CourtUrl?.Trim() ?? string.Empty;
        target.NextHearingAt = source.NextHearingAt;
        target.Claimant = source.Claimant?.Trim() ?? string.Empty;
        target.Defendant = source.Defendant?.Trim() ?? string.Empty;
        target.ThirdParties = source.ThirdParties?.Trim() ?? string.Empty;
        target.Representatives = source.Representatives?.Trim() ?? string.Empty;
        target.JudgeName = source.JudgeName?.Trim() ?? string.Empty;
        target.Courtroom = source.Courtroom?.Trim() ?? string.Empty;
        target.CourtSecretaryContacts = source.CourtSecretaryContacts?.Trim() ?? string.Empty;
        target.ClaimFiledAt = source.ClaimFiledAt;
        target.CourtDecisionAt = source.CourtDecisionAt;
        target.AppealDeadlineAt = ResolveProceduralDeadline(
            source.AppealDeadlineAt,
            target.CourtDecisionAt,
            previousCourtDecisionAt,
            previousAppealDeadlineAt,
            monthsToAdd: 1);
        target.CassationDeadlineAt = ResolveProceduralDeadline(
            source.CassationDeadlineAt,
            target.CourtDecisionAt,
            previousCourtDecisionAt,
            previousCassationDeadlineAt,
            monthsToAdd: 3);
        target.MotionDeadlineAt = source.MotionDeadlineAt;
        target.ObjectionDeadlineAt = source.ObjectionDeadlineAt;
        target.DocumentsSummary = source.DocumentsSummary?.Trim() ?? string.Empty;
        target.EventHistory = source.EventHistory?.Trim() ?? string.Empty;
        target.Strategy = source.Strategy?.Trim() ?? string.Empty;
        target.InactivityRemindersEnabled = source.InactivityRemindersEnabled;
        target.IsHiddenFromExecutor = source.IsHiddenFromExecutor;
    }

    private static void ApplyHiddenFromExecutorMetadata(
        ClientCase clientCase,
        bool isHidden,
        int? actorUserId,
        string actorName)
    {
        clientCase.IsHiddenFromExecutor = isHidden;
        if (isHidden)
        {
            clientCase.HiddenFromExecutorAt = DateTime.UtcNow;
            clientCase.HiddenFromExecutorByUserId = actorUserId;
            clientCase.HiddenFromExecutorByUserName = Truncate(actorName, 256);
            return;
        }

        clientCase.HiddenFromExecutorAt = null;
        clientCase.HiddenFromExecutorByUserId = null;
        clientCase.HiddenFromExecutorByUserName = string.Empty;
        clientCase.AccessRequestedAt = null;
        clientCase.AccessRequestedByUserId = null;
        clientCase.AccessRequestedByUserName = string.Empty;
        clientCase.AccessRequestReason = string.Empty;
    }

    private static DateTime? ResolveProceduralDeadline(
        DateTime? requestedDeadline,
        DateTime? courtDecisionAt,
        DateTime? previousCourtDecisionAt,
        DateTime? previousDeadline,
        int monthsToAdd)
    {
        if (requestedDeadline.HasValue)
        {
            return requestedDeadline.Value;
        }

        if (!courtDecisionAt.HasValue)
        {
            return null;
        }

        if (previousDeadline.HasValue && SameDate(previousCourtDecisionAt, courtDecisionAt))
        {
            return null;
        }

        if (previousCourtDecisionAt.HasValue && previousDeadline.HasValue)
        {
            var previousCalculated = ProceduralDeadlineCalculator.CalculateByMonths(previousCourtDecisionAt.Value, monthsToAdd);
            if (!SameDate(previousDeadline, previousCalculated))
            {
                return null;
            }
        }

        return ProceduralDeadlineCalculator.CalculateByMonths(courtDecisionAt.Value, monthsToAdd);
    }

    private static bool SameDate(DateTime? left, DateTime? right)
        => left.HasValue == right.HasValue && (!left.HasValue || left.Value.Date == right!.Value.Date);

    private async Task ApplyFinanceSummaryAsync(IReadOnlyCollection<ClientCase> cases, CancellationToken ct)
    {
        var caseIds = cases
            .Select(item => item.Id)
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
        if (caseIds.Length == 0)
        {
            return;
        }

        var caseById = cases.ToDictionary(item => item.Id);
        var caseIdSet = caseIds.ToHashSet();
        var payments = await _db.Payments
            .AsNoTracking()
            .Include(payment => payment.InvoiceClientCases)
            .Where(payment => payment.Status != PaymentStatus.Cancelled)
            .Where(payment =>
                (payment.ClientCaseId.HasValue && caseIds.Contains(payment.ClientCaseId.Value)) ||
                payment.InvoiceClientCases.Any(link => caseIds.Contains(link.ClientCaseId)))
            .ToListAsync(ct);

        foreach (var payment in InvoiceManagedPaymentRules.FilterVisiblePayments(payments))
        {
            var linkedCaseIds = payment.InvoiceClientCases
                .Select(link => link.ClientCaseId)
                .Concat(payment.ClientCaseId.HasValue ? new[] { payment.ClientCaseId.Value } : Array.Empty<int>())
                .Where(caseIdSet.Contains)
                .Distinct();
            var outstanding = payment.Amount > payment.PaidAmount
                ? Math.Round(payment.Amount - payment.PaidAmount, 2)
                : 0m;

            foreach (var caseId in linkedCaseIds)
            {
                if (!caseById.TryGetValue(caseId, out var clientCase))
                {
                    continue;
                }

                if (payment.Type == PaymentType.Income)
                {
                    clientCase.IncomeTotal += payment.Amount;
                    clientCase.IncomePaid += payment.PaidAmount;
                    clientCase.IncomeOutstanding += outstanding;
                    clientCase.IncomeCount += 1;
                }
                else
                {
                    clientCase.ExpenseTotal += payment.Amount;
                    clientCase.ExpensePaid += payment.PaidAmount;
                    clientCase.ExpenseOutstanding += outstanding;
                    clientCase.ExpenseCount += 1;
                }
            }
        }
    }

    private async Task ApplyTaskSummaryAsync(IReadOnlyCollection<ClientCase> cases, CancellationToken ct)
    {
        var caseIds = cases
            .Select(item => item.Id)
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
        if (caseIds.Length == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var summaryByCaseId = await _db.TaskJournalItems
            .AsNoTracking()
            .Where(task => task.ClientCaseId.HasValue && caseIds.Contains(task.ClientCaseId.Value))
            .GroupBy(task => task.ClientCaseId!.Value)
            .Select(group => new
            {
                CaseId = group.Key,
                Total = group.Count(),
                Open = group.Count(task => task.Status != "done" && task.Status != "cancelled"),
                Completed = group.Count(task => task.Status == "done" || task.Status == "cancelled"),
                Overdue = group.Count(task => task.Status != "done" && task.Status != "cancelled" && task.DueAt < now),
                LastTaskAt = group.Max(task => task.UpdatedAt ?? task.CreatedAt),
            })
            .ToDictionaryAsync(item => item.CaseId, ct);

        foreach (var clientCase in cases)
        {
            if (!summaryByCaseId.TryGetValue(clientCase.Id, out var summary))
            {
                continue;
            }

            clientCase.OpenTaskCount = summary.Open;
            clientCase.CompletedTaskCount = summary.Completed;
            clientCase.OverdueTaskCount = summary.Overdue;
            clientCase.LastTaskAt = summary.LastTaskAt;
            clientCase.TaskProgressPercent = summary.Total == 0
                ? 0
                : (int)Math.Round(summary.Completed * 100m / summary.Total, MidpointRounding.AwayFromZero);
        }
    }

    private async Task<(CaseDocumentTemplateDefinition Template, string? Error, bool NotFound)> ResolveCaseDocumentTemplateDefinitionAsync(
        GenerateCaseDocumentTemplateRequest? request,
        CancellationToken ct)
    {
        var templates = await GetCaseDocumentTemplateLibraryAsync(ct);
        var requestedTemplateKey = NormalizeOptional(request?.TemplateKey);
        if (requestedTemplateKey is not null)
        {
            var normalizedKey = NormalizeDocumentTemplateCode(requestedTemplateKey);
            if (normalizedKey is null)
            {
                return (default, "Некорректный код шаблона.", false);
            }

            var template = templates.FirstOrDefault(item =>
                item.IsActive && string.Equals(item.Key, normalizedKey, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(template.Key))
            {
                return (template, null, false);
            }

            var legacyKey = NormalizeCaseDocumentTemplateKey(requestedTemplateKey, request?.DocumentType);
            if (!string.Equals(legacyKey, normalizedKey, StringComparison.OrdinalIgnoreCase))
            {
                template = templates.FirstOrDefault(item =>
                    item.IsActive && string.Equals(item.Key, legacyKey, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(template.Key))
                {
                    return (template, null, false);
                }
            }

            return (default, "Активный шаблон документа не найден.", true);
        }

        var inferredKey = NormalizeCaseDocumentTemplateKey(null, request?.DocumentType);
        var inferredTemplate = templates.FirstOrDefault(item =>
            item.IsActive && string.Equals(item.Key, inferredKey, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(inferredTemplate.Key))
        {
            return (inferredTemplate, null, false);
        }

        return (CaseDocumentTemplateDefinitions[0], null, false);
    }

    private static CaseDocumentTemplate BuildCaseDocumentTemplate(
        ClientCase clientCase,
        GenerateCaseDocumentTemplateRequest? request,
        CaseDocumentTemplateDefinition definition)
    {
        var documentType = NormalizeOptional(request?.DocumentType) ?? definition.DocumentType;
        var status = NormalizeRequired(request?.Status, "Подготовлен");
        var caseNumber = !string.IsNullOrWhiteSpace(clientCase.CaseNumber)
            ? clientCase.CaseNumber.Trim()
            : clientCase.Id.ToString();
        var fileName = $"{definition.Title} - дело {caseNumber}.html";

        return new CaseDocumentTemplate(
            $"template-{definition.Key}",
            documentType,
            status,
            fileName,
            BuildCaseDocumentTemplateHtml(clientCase, definition, documentType));
    }

    private async Task<IReadOnlyList<CaseDocumentTemplateDefinition>> GetCaseDocumentTemplateLibraryAsync(CancellationToken ct)
    {
        var rawJson = await _db.SystemSettings
            .AsNoTracking()
            .Where(setting => setting.Key == CaseDocumentTemplatesSettingKey)
            .Select(setting => setting.Value)
            .FirstOrDefaultAsync(ct);

        var storage = DeserializeCaseDocumentTemplateLibrary(rawJson);
        var templates = storage?.Templates
            .Select(static item => new CaseDocumentTemplateDefinition(
                item.Key,
                item.Label,
                item.DocumentType,
                item.Heading,
                NormalizeCaseDocumentTemplateCaseTypes(item.CaseTypes),
                item.Description,
                item.Content,
                item.Version <= 0 ? 1 : item.Version,
                item.IsActive,
                item.UpdatedAt))
            .ToList();

        return NormalizeCaseDocumentTemplateLibrary(
            templates is { Count: > 0 }
                ? templates
                : CaseDocumentTemplateDefinitions.ToList());
    }

    private async Task SaveCaseDocumentTemplateLibraryAsync(
        IReadOnlyCollection<CaseDocumentTemplateDefinition> templates,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var storage = new CaseDocumentTemplateLibraryStorage
        {
            Templates = NormalizeCaseDocumentTemplateLibrary(templates)
                .Select(template => new CaseDocumentTemplateStorage
                {
                    Key = template.Key,
                    Label = template.Title,
                    DocumentType = template.DocumentType,
                    Heading = template.Heading,
                    CaseTypes = NormalizeCaseDocumentTemplateCaseTypes(template.CaseTypes),
                    Description = template.Description,
                    Content = template.Content,
                    Version = template.Version <= 0 ? 1 : template.Version,
                    IsActive = template.IsActive,
                    UpdatedAt = template.UpdatedAt ?? now,
                })
                .ToList(),
        };
        var value = JsonSerializer.Serialize(storage, CaseDocumentTemplateJsonOptions);
        var setting = await _db.SystemSettings.FirstOrDefaultAsync(item => item.Key == CaseDocumentTemplatesSettingKey, ct);
        if (setting is null)
        {
            _db.SystemSettings.Add(new SystemSetting
            {
                Key = CaseDocumentTemplatesSettingKey,
                Value = value,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        else
        {
            setting.Value = value;
            setting.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(ct);
    }

    private static CaseDocumentTemplateLibraryStorage? DeserializeCaseDocumentTemplateLibrary(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<CaseDocumentTemplateLibraryStorage>(rawJson, CaseDocumentTemplateJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<CaseDocumentTemplateDefinition> NormalizeCaseDocumentTemplateLibrary(
        IEnumerable<CaseDocumentTemplateDefinition> templates)
    {
        var result = new List<CaseDocumentTemplateDefinition>();
        var usedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var template in templates)
        {
            var key = NormalizeDocumentTemplateCode(template.Key);
            if (key is null || !usedKeys.Add(key))
            {
                continue;
            }

            var title = NormalizeRequired(template.Title, template.Heading);
            title = NormalizeRequired(title, key);
            var documentType = NormalizeRequired(template.DocumentType, "Документ");
            result.Add(template with
            {
                Key = key,
                Title = title,
                DocumentType = documentType,
                Heading = NormalizeRequired(template.Heading, title),
                CaseTypes = NormalizeCaseDocumentTemplateCaseTypes(template.CaseTypes),
                Version = template.Version <= 0 ? 1 : template.Version,
                IsActive = template.IsActive,
            });
        }

        return result.Count > 0
            ? result
            : CaseDocumentTemplateDefinitions.ToList();
    }

    private static List<string> NormalizeCaseDocumentTemplateCaseTypes(IEnumerable<string>? values)
    {
        if (values is null)
        {
            return new List<string>();
        }

        return values
            .SelectMany(static value => (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(NormalizeOptional)
            .Where(static value => value is not null)
            .Select(static value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.Create(new CultureInfo("ru-RU"), ignoreCase: true))
            .ToList();
    }

    private static bool IsCaseDocumentTemplateApplicableToCase(CaseDocumentTemplateDefinition template, ClientCase clientCase)
    {
        var caseTypes = NormalizeCaseDocumentTemplateCaseTypes(template.CaseTypes);
        if (caseTypes.Count == 0)
        {
            return true;
        }

        var caseType = NormalizeOptional(clientCase.CaseType);
        return caseType is not null && caseTypes.Contains(caseType, StringComparer.OrdinalIgnoreCase);
    }

    private static (CaseDocumentTemplateDefinition Template, string? Error) BuildCaseDocumentTemplateDefinition(
        UpsertCaseDocumentTemplateRequest? request,
        CaseDocumentTemplateDefinition? existing)
    {
        var key = existing?.Key ?? NormalizeDocumentTemplateCode(request?.Key);
        if (key is null)
        {
            return (default, "Укажите код шаблона латиницей: буквы, цифры, дефис или подчеркивание.");
        }

        var label = NormalizeOptional(request?.Label)
            ?? NormalizeOptional(request?.Name)
            ?? existing?.Title;
        if (string.IsNullOrWhiteSpace(label))
        {
            return (default, "Укажите название шаблона.");
        }

        var documentType = NormalizeOptional(request?.DocumentType) ?? existing?.DocumentType ?? "Документ";
        var heading = NormalizeOptional(request?.Heading) ?? existing?.Heading ?? label;
        var caseTypes = request?.CaseTypes is null
            ? NormalizeCaseDocumentTemplateCaseTypes(existing?.CaseTypes)
            : NormalizeCaseDocumentTemplateCaseTypes(request.CaseTypes);
        var now = DateTime.UtcNow;

        return (new CaseDocumentTemplateDefinition(
            key,
            label,
            documentType,
            heading,
            caseTypes,
            request?.Description ?? existing?.Description,
            request?.Content ?? request?.Html ?? existing?.Content,
            existing.HasValue ? existing.Value.Version + 1 : 1,
            request?.IsActive ?? existing?.IsActive ?? true,
            now), null);
    }

    private static CaseDocumentTemplateDefinitionDto MapDocumentTemplateDefinition(CaseDocumentTemplateDefinition template)
        => new()
        {
            Key = template.Key,
            Label = template.Title,
            DocumentType = template.DocumentType,
            Heading = template.Heading,
            CaseTypes = NormalizeCaseDocumentTemplateCaseTypes(template.CaseTypes),
            Description = template.Description,
            Content = template.Content,
            Version = template.Version <= 0 ? 1 : template.Version,
            IsActive = template.IsActive,
            UpdatedAt = template.UpdatedAt,
        };

    private static CaseDocumentTemplateDefinition GetCaseDocumentTemplateDefinition(string key)
    {
        var definition = CaseDocumentTemplateDefinitions.FirstOrDefault(item => item.Key == key);
        return string.IsNullOrWhiteSpace(definition.Key) ? CaseDocumentTemplateDefinitions[0] : definition;
    }

    private static string NormalizeCaseDocumentTemplateKey(string? templateKey, string? documentType)
    {
        var normalized = templateKey?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            return normalized switch
            {
                "claim" or "isk" => "claim",
                "motion" => "motion",
                "objection" => "objection",
                "court-letter" or "letter" => "court-letter",
                _ => "case-summary",
            };
        }

        var type = documentType?.Trim().ToLowerInvariant() ?? string.Empty;
        if (type.Contains("иск", StringComparison.OrdinalIgnoreCase)) return "claim";
        if (type.Contains("ходатай", StringComparison.OrdinalIgnoreCase)) return "motion";
        if (type.Contains("возраж", StringComparison.OrdinalIgnoreCase)) return "objection";
        if (type.Contains("переписк", StringComparison.OrdinalIgnoreCase) || type.Contains("письм", StringComparison.OrdinalIgnoreCase)) return "court-letter";
        return "case-summary";
    }

    private static string? NormalizeDocumentTemplateCode(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 64)
        {
            return null;
        }

        return normalized.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_')
            ? normalized
            : null;
    }

    private static string BuildCaseDocumentTemplateHtml(
        ClientCase clientCase,
        CaseDocumentTemplateDefinition definition,
        string documentType)
    {
        var ownerName = ResolveCaseOwnerName(clientCase);
        var responsibleName = clientCase.ResponsibleUser?.FullName ?? string.Empty;
        var now = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(definition.Content))
        {
            return BuildCustomCaseDocumentTemplateHtml(clientCase, definition, ownerName, responsibleName, documentType, now);
        }

        var html = new StringBuilder();
        html.Append("""
<!doctype html>
<html lang="ru">
<head>
  <meta charset="utf-8">
  <title>
""");
        html.Append(E(definition.Title));
        html.Append("""
</title>
  <style>
    body { font-family: Arial, sans-serif; margin: 32px; color: #111827; line-height: 1.45; }
    .document-template { max-width: 780px; margin: 0 auto; }
    h1 { font-size: 22px; text-align: center; margin: 28px 0; text-transform: uppercase; }
    h2 { font-size: 15px; margin: 24px 0 8px; }
    .meta { width: 100%; border-collapse: collapse; margin: 16px 0 24px; }
    .meta td { border: 1px solid #d1d5db; padding: 8px 10px; vertical-align: top; }
    .meta td:first-child { width: 34%; font-weight: 700; background: #f9fafb; }
    .recipient { text-align: right; margin-bottom: 28px; }
    .body p { margin: 0 0 12px; }
    .signature { margin-top: 42px; display: flex; justify-content: space-between; gap: 24px; }
    .muted { color: #6b7280; font-size: 12px; }
  </style>
</head>
<body>
  <div class="document-template">
""");

        if (!string.IsNullOrWhiteSpace(clientCase.CourtName))
        {
            html.AppendLine("""    <div class="recipient">""");
            html.Append("      <div>В ");
            html.Append(E(clientCase.CourtName));
            html.AppendLine("</div>");
            if (!string.IsNullOrWhiteSpace(clientCase.CourtCaseNumber))
            {
                html.Append("      <div>по делу № ");
                html.Append(E(clientCase.CourtCaseNumber));
                html.AppendLine("</div>");
            }
            html.AppendLine("    </div>");
        }

        html.Append("    <h1>");
        html.Append(E(definition.Heading));
        html.AppendLine("</h1>");
        html.AppendLine("    <table class=\"meta\">");
        AppendTemplateRow(html, "Карточка дела", clientCase.Title);
        AppendTemplateRow(html, "Номер дела", clientCase.CaseNumber);
        AppendTemplateRow(html, "Клиент / доверитель", ownerName);
        AppendTemplateRow(html, "Тип дела", clientCase.CaseType);
        AppendTemplateRow(html, "Стадия", clientCase.Stage);
        AppendTemplateRow(html, "Ответственный", responsibleName);
        AppendTemplateRow(html, "Истец / заявитель", clientCase.Claimant);
        AppendTemplateRow(html, "Ответчик / должник", clientCase.Defendant);
        AppendTemplateRow(html, "Третьи лица", clientCase.ThirdParties);
        AppendTemplateRow(html, "Представители", clientCase.Representatives);
        AppendTemplateRow(html, "Судья", clientCase.JudgeName);
        AppendTemplateRow(html, "Ближайшее заседание", FormatTemplateDateTime(clientCase.NextHearingAt));
        AppendTemplateRow(html, "Дата подачи заявления", FormatTemplateDate(clientCase.ClaimFiledAt));
        AppendTemplateRow(html, "Дата решения суда", FormatTemplateDate(clientCase.CourtDecisionAt));
        AppendTemplateRow(html, "Срок апелляции", FormatTemplateDate(clientCase.AppealDeadlineAt));
        AppendTemplateRow(html, "Срок кассации", FormatTemplateDate(clientCase.CassationDeadlineAt));
        html.AppendLine("    </table>");

        html.AppendLine("    <div class=\"body\">");
        html.AppendLine("      <h2>Текст документа</h2>");
        foreach (var paragraph in BuildTemplateParagraphs(definition.Key, clientCase, ownerName, documentType))
        {
            html.Append("      <p>");
            html.Append(E(paragraph));
            html.AppendLine("</p>");
        }
        html.AppendLine("    </div>");
        html.AppendLine("    <div class=\"signature\">");
        html.Append("      <div>Дата: ");
        html.Append(E(now.ToString("dd.MM.yyyy")));
        html.AppendLine("</div>");
        html.AppendLine("      <div>Подпись: ____________________</div>");
        html.AppendLine("    </div>");
        html.Append("    <p class=\"muted\">Сформировано автоматически из карточки дела CRM ");
        html.Append(E(now.ToString("dd.MM.yyyy HH:mm")));
        html.AppendLine(".</p>");
        html.AppendLine("  </div>");
        html.AppendLine("</body>");
        html.AppendLine("</html>");

        return html.ToString();
    }

    private static string BuildCustomCaseDocumentTemplateHtml(
        ClientCase clientCase,
        CaseDocumentTemplateDefinition definition,
        string ownerName,
        string responsibleName,
        string documentType,
        DateTime now)
    {
        var renderedContent = ReplaceCaseDocumentTemplateTokens(
            definition.Content ?? string.Empty,
            clientCase,
            ownerName,
            responsibleName,
            documentType,
            now);
        if (renderedContent.Contains("<html", StringComparison.OrdinalIgnoreCase))
        {
            return renderedContent;
        }

        var contentHtml = renderedContent.Contains('<')
            ? renderedContent
            : string.Join("<br>", renderedContent
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Select(E));

        var html = new StringBuilder();
        html.Append("""
<!doctype html>
<html lang="ru">
<head>
  <meta charset="utf-8">
  <title>
""");
        html.Append(E(definition.Title));
        html.Append("""
</title>
  <style>
    body { font-family: Arial, sans-serif; margin: 32px; color: #111827; line-height: 1.45; }
    .document-template { max-width: 780px; margin: 0 auto; }
    h1 { font-size: 22px; text-align: center; margin: 28px 0; text-transform: uppercase; }
    .body { margin-top: 24px; }
    .muted { color: #6b7280; font-size: 12px; margin-top: 36px; }
  </style>
</head>
<body>
  <div class="document-template">
    <h1>
""");
        html.Append(E(definition.Heading));
        html.AppendLine("</h1>");
        html.AppendLine("    <div class=\"body\">");
        html.Append(contentHtml);
        html.AppendLine();
        html.Append("    </div><p class=\"muted\">Сформировано автоматически из карточки дела CRM ");
        html.Append(E(now.ToString("dd.MM.yyyy HH:mm")));
        html.AppendLine(".</p>");
        html.AppendLine("  </div>");
        html.AppendLine("</body>");
        html.AppendLine("</html>");
        return html.ToString();
    }

    private static string ReplaceCaseDocumentTemplateTokens(
        string content,
        ClientCase clientCase,
        string ownerName,
        string responsibleName,
        string documentType,
        DateTime now)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["{{case.id}}"] = clientCase.Id.ToString(CultureInfo.InvariantCulture),
            ["{{case.title}}"] = clientCase.Title,
            ["{{case.number}}"] = clientCase.CaseNumber ?? string.Empty,
            ["{{case.type}}"] = clientCase.CaseType ?? string.Empty,
            ["{{case.stage}}"] = clientCase.Stage ?? string.Empty,
            ["{{case.priority}}"] = clientCase.Priority ?? string.Empty,
            ["{{client.name}}"] = ownerName,
            ["{{responsible.name}}"] = responsibleName,
            ["{{court.name}}"] = clientCase.CourtName ?? string.Empty,
            ["{{court.caseNumber}}"] = clientCase.CourtCaseNumber ?? string.Empty,
            ["{{claimant}}"] = clientCase.Claimant ?? string.Empty,
            ["{{defendant}}"] = clientCase.Defendant ?? string.Empty,
            ["{{thirdParties}}"] = clientCase.ThirdParties ?? string.Empty,
            ["{{representatives}}"] = clientCase.Representatives ?? string.Empty,
            ["{{judge.name}}"] = clientCase.JudgeName ?? string.Empty,
            ["{{nextHearingAt}}"] = FormatTemplateDateTime(clientCase.NextHearingAt),
            ["{{claimFiledAt}}"] = FormatTemplateDate(clientCase.ClaimFiledAt),
            ["{{courtDecisionAt}}"] = FormatTemplateDate(clientCase.CourtDecisionAt),
            ["{{appealDeadlineAt}}"] = FormatTemplateDate(clientCase.AppealDeadlineAt),
            ["{{cassationDeadlineAt}}"] = FormatTemplateDate(clientCase.CassationDeadlineAt),
            ["{{document.type}}"] = documentType,
            ["{{today}}"] = now.ToString("dd.MM.yyyy"),
            ["{{now}}"] = now.ToString("dd.MM.yyyy HH:mm"),
        };

        var result = content;
        foreach (var pair in values)
        {
            result = result.Replace(pair.Key, pair.Value, StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    private static IReadOnlyList<string> BuildTemplateParagraphs(
        string templateKey,
        ClientCase clientCase,
        string ownerName,
        string documentType)
        => templateKey switch
        {
            "claim" =>
            [
                $"В интересах доверителя {ValueOrDash(ownerName)} подготовлено исковое заявление по делу «{ValueOrDash(clientCase.Title)}».",
                "Прошу суд принять заявление к производству, рассмотреть приложенные материалы и вынести судебный акт с учетом фактических обстоятельств дела.",
                "Основания, требования и перечень приложений необходимо уточнить перед отправкой документа."
            ],
            "motion" =>
            [
                $"По делу «{ValueOrDash(clientCase.Title)}» заявляется ходатайство.",
                "Прошу суд рассмотреть настоящее ходатайство и приобщить его к материалам дела.",
                "Конкретную просьбу и правовое основание необходимо заполнить перед направлением документа."
            ],
            "objection" =>
            [
                $"По делу «{ValueOrDash(clientCase.Title)}» подготовлены возражения.",
                "С доводами противоположной стороны не согласны по основаниям, которые подлежат уточнению в тексте документа.",
                "Прошу учесть настоящие возражения при рассмотрении дела."
            ],
            "court-letter" =>
            [
                $"Направляем информацию по делу «{ValueOrDash(clientCase.Title)}».",
                "Просим принять документ к сведению и при необходимости приобщить к материалам дела.",
                "Реквизиты адресата, исходящий номер и приложения необходимо уточнить перед отправкой."
            ],
            "power-of-attorney" =>
            [
                $"Доверитель {ValueOrDash(ownerName)} уполномочивает представителя по делу «{ValueOrDash(clientCase.Title)}».",
                $"Представитель вправе готовить, подписывать и подавать процессуальные документы, получать материалы дела, участвовать в заседаниях и совершать иные действия, необходимые для защиты интересов доверителя. Ответственный по делу: {ValueOrDash(clientCase.ResponsibleUser?.FullName)}.",
                "Срок действия, паспортные данные доверителя и представителя, а также перечень специальных полномочий необходимо проверить и заполнить перед подписанием."
            ],
            "application" =>
            [
                $"По делу «{ValueOrDash(clientCase.Title)}» подготовлено заявление от имени {ValueOrDash(ownerName)}.",
                $"Прошу рассмотреть настоящее заявление, учесть обстоятельства дела и приобщить документ к материалам. Адресат: {ValueOrDash(clientCase.CourtName)}.",
                "Просительную часть, приложения и процессуальные основания необходимо уточнить перед направлением."
            ],
            "procedural-document" =>
            [
                $"Процессуальный документ подготовлен по делу «{ValueOrDash(clientCase.Title)}».",
                $"Документ связан со стадией «{ValueOrDash(clientCase.Stage)}», ближайшее заседание: {ValueOrDash(FormatTemplateDateTime(clientCase.NextHearingAt))}.",
                "Перед подачей необходимо проверить процессуальный срок, адресата, полномочия подписанта, список приложений и доказательств."
            ],
            _ =>
            [
                $"Справка сформирована по карточке дела «{ValueOrDash(clientCase.Title)}».",
                $"Клиент / доверитель: {ValueOrDash(ownerName)}.",
                $"Тип документа: {ValueOrDash(documentType)}. Данные предназначены для подготовки рабочего документа и последующей ручной проверки."
            ],
        };

    private static void AppendTemplateRow(StringBuilder html, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        html.Append("      <tr><td>");
        html.Append(E(label));
        html.Append("</td><td>");
        html.Append(E(value));
        html.AppendLine("</td></tr>");
    }

    private static string ResolveCaseOwnerName(ClientCase clientCase)
    {
        if (clientCase.Client is not null && !string.IsNullOrWhiteSpace(clientCase.Client.Name))
        {
            return clientCase.Client.Name;
        }

        if (clientCase.LegalEntity is not null)
        {
            if (!string.IsNullOrWhiteSpace(clientCase.LegalEntity.ShortName))
            {
                return clientCase.LegalEntity.ShortName;
            }

            if (!string.IsNullOrWhiteSpace(clientCase.LegalEntity.FullName))
            {
                return clientCase.LegalEntity.FullName;
            }
        }

        return string.Empty;
    }

    private static string FormatTemplateDate(DateTime? value)
        => value.HasValue ? value.Value.ToString("dd.MM.yyyy") : string.Empty;

    private static string FormatTemplateDateTime(DateTime? value)
        => value.HasValue ? value.Value.ToString("dd.MM.yyyy HH:mm") : string.Empty;

    private static string ValueOrDash(string? value)
        => string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();

    private static string E(string? value)
        => WebUtility.HtmlEncode(value ?? string.Empty);

    private static IReadOnlyList<ClientCaseDocumentDto> MapDocuments(
        IReadOnlyCollection<ClientCaseDocument> documents,
        IReadOnlyCollection<CaseDocumentTemplateDefinition>? templates = null)
    {
        var latestVersions = documents
            .GroupBy(item => item.DocumentKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Max(item => item.Version), StringComparer.OrdinalIgnoreCase);

        return documents
            .Select(item => MapDocument(
                item,
                latestVersions.TryGetValue(item.DocumentKey, out var latest) && item.Version == latest,
                templates))
            .ToList();
    }

    private async Task<IReadOnlyList<ClientCaseDocumentDto>> MapDocumentsWithLatestAsync(
        IReadOnlyCollection<ClientCaseDocument> documents,
        CancellationToken ct)
    {
        if (documents.Count == 0)
        {
            return Array.Empty<ClientCaseDocumentDto>();
        }

        var caseIds = documents.Select(item => item.ClientCaseId).Distinct().ToArray();
        var documentKeys = documents.Select(item => item.DocumentKey).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var latestVersions = await _db.ClientCaseDocuments
            .AsNoTracking()
            .Where(item => caseIds.Contains(item.ClientCaseId) && documentKeys.Contains(item.DocumentKey))
            .GroupBy(item => new { item.ClientCaseId, item.DocumentKey })
            .Select(group => new
            {
                group.Key.ClientCaseId,
                group.Key.DocumentKey,
                Version = group.Max(item => item.Version),
            })
            .ToListAsync(ct);
        var latestByKey = latestVersions.ToDictionary(
            item => $"{item.ClientCaseId}:{item.DocumentKey}",
            item => item.Version,
            StringComparer.OrdinalIgnoreCase);

        var templates = await GetCaseDocumentTemplateLibraryAsync(ct);

        return documents
            .Select(item => MapDocument(
                item,
                latestByKey.TryGetValue($"{item.ClientCaseId}:{item.DocumentKey}", out var latest) && item.Version == latest,
                templates))
            .ToList();
    }

    private static ClientCaseDocumentDto MapDocument(
        ClientCaseDocument document,
        bool isLatestVersion,
        IReadOnlyCollection<CaseDocumentTemplateDefinition>? templates = null)
    {
        var template = ResolveDocumentTemplate(document.DocumentKey, templates);

        return new ClientCaseDocumentDto
        {
            Id = document.Id,
            ClientCaseId = document.ClientCaseId,
            DocumentKey = document.DocumentKey,
            TemplateKey = template?.Key,
            TemplateLabel = template?.Title,
            SourceTaskId = ResolveDocumentSourceTaskId(document.DocumentKey),
            SourceEventId = ResolveDocumentSourceId(document.DocumentKey, "court-event-"),
            SourceWorkOrderId = ResolveDocumentSourceId(document.DocumentKey, "work-order-"),
            SourceInvoiceId = ResolveDocumentSourceId(document.DocumentKey, "invoice-"),
            SourceStage = ResolveDocumentSourceStage(document.DocumentKey),
            Version = document.Version,
            DocumentType = document.DocumentType,
            Status = document.Status,
            OriginalFileName = document.OriginalFileName,
            ContentType = document.ContentType,
            SizeBytes = document.SizeBytes,
            UploadedByUserId = document.UploadedByUserId,
            UploadedByName = document.UploadedByName,
            UploadedAt = document.UploadedAt,
            IsLatestVersion = isLatestVersion,
        };
    }

    private static CaseDocumentTemplateDefinition? ResolveDocumentTemplate(
        string documentKey,
        IReadOnlyCollection<CaseDocumentTemplateDefinition>? templates = null)
    {
        const string templatePrefix = "template-";
        if (!documentKey.StartsWith(templatePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var templateKey = documentKey[templatePrefix.Length..];
        var definitions = templates is { Count: > 0 }
            ? templates
            : CaseDocumentTemplateDefinitions;
        var definition = definitions.FirstOrDefault(item =>
            string.Equals(item.Key, templateKey, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(definition.Key) ? null : definition;
    }

    private static string BuildWorkOrderDocumentKey(int workOrderId)
        => $"work-order-{workOrderId}";

    private static string BuildInvoiceDocumentKey(int paymentId)
        => $"invoice-{paymentId}";

    private static string BuildCourtEventDocumentKey(int courtEventId)
        => $"court-event-{courtEventId}";

    private static int? ResolveDocumentSourceTaskId(string documentKey)
    {
        const string taskPrefix = "task-";
        if (!documentKey.StartsWith(taskPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rest = documentKey[taskPrefix.Length..];
        var separatorIndex = rest.IndexOf('-', StringComparison.Ordinal);
        if (separatorIndex <= 0)
        {
            return null;
        }

        return int.TryParse(rest[..separatorIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out var taskId)
            ? taskId
            : null;
    }

    private static int? ResolveDocumentSourceId(string documentKey, string prefix)
    {
        if (!documentKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rest = documentKey[prefix.Length..];
        var separatorIndex = rest.IndexOf('-', StringComparison.Ordinal);
        var rawId = separatorIndex < 0 ? rest : rest[..separatorIndex];
        return int.TryParse(rawId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sourceId)
            ? sourceId
            : null;
    }

    private static string? ResolveDocumentSourceStage(string documentKey)
    {
        const string stagePrefix = "stage-";
        if (!documentKey.StartsWith(stagePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rest = documentKey[stagePrefix.Length..];
        var separatorIndex = rest.IndexOf('-', StringComparison.Ordinal);
        var sourceStage = separatorIndex < 0 ? rest : rest[..separatorIndex];
        return string.IsNullOrWhiteSpace(sourceStage) ? null : sourceStage;
    }

    private static ClientCaseSummaryDto MapCaseSummary(ClientCase clientCase)
    {
        var paidAmount = clientCase.IncomePaid;
        var debtAmount = clientCase.FeeAmount > 0
            ? Math.Max(clientCase.FeeAmount - paidAmount, 0m)
            : clientCase.IncomeOutstanding;

        return new ClientCaseSummaryDto
        {
            Id = clientCase.Id,
            Title = clientCase.Title,
            CaseNumber = clientCase.CaseNumber,
            Status = clientCase.Status.ToString(),
            StatusLabel = FormatCaseStatus(clientCase.Status),
            Priority = clientCase.Priority,
            PriorityLabel = FormatCasePriority(clientCase.Priority),
            Stage = clientCase.Stage,
            OwnerName = clientCase.Client?.Name
                ?? clientCase.LegalEntity?.ShortName
                ?? clientCase.LegalEntity?.FullName
                ?? string.Empty,
            ResponsibleUserId = clientCase.ResponsibleUserId,
            ResponsibleName = FormatCaseResponsible(clientCase.ResponsibleUser),
            StartDate = clientCase.StartDate,
            DueDate = clientCase.DueDate,
            NextHearingAt = clientCase.NextHearingAt,
            OpenTaskCount = clientCase.OpenTaskCount,
            OverdueTaskCount = clientCase.OverdueTaskCount,
            CompletedTaskCount = clientCase.CompletedTaskCount,
            TaskProgressPercent = clientCase.TaskProgressPercent,
            FeeAmount = clientCase.FeeAmount,
            PaidAmount = paidAmount,
            DebtAmount = debtAmount,
            LastTaskAt = clientCase.LastTaskAt,
            LastViewedAt = clientCase.LastViewedAt,
        };
    }

    private static ClientCaseAccessRequestListItemDto MapClientCaseAccessRequest(ClientCase clientCase)
        => new()
        {
            ClientCaseId = clientCase.Id,
            CaseNumber = clientCase.CaseNumber,
            Title = clientCase.Title,
            OwnerName = clientCase.Client?.Name
                ?? clientCase.LegalEntity?.ShortName
                ?? clientCase.LegalEntity?.FullName
                ?? string.Empty,
            ResponsibleUserId = clientCase.ResponsibleUserId,
            ResponsibleName = FormatCaseResponsible(clientCase.ResponsibleUser),
            RequestedAt = clientCase.AccessRequestedAt!.Value,
            RequestedByUserId = clientCase.AccessRequestedByUserId,
            RequestedByUserName = clientCase.AccessRequestedByUserName,
            Reason = clientCase.AccessRequestReason,
            IsHiddenFromExecutor = clientCase.IsHiddenFromExecutor,
        };

    private async Task<Dictionary<int, Contract>> LoadFinanceContractsAsync(
        IReadOnlyCollection<Payment> visiblePayments,
        IReadOnlyCollection<int> invoicePaymentIds,
        IReadOnlyCollection<int> paymentIds,
        CancellationToken ct)
    {
        var contractsById = visiblePayments
            .Where(payment => payment.Contract is not null)
            .Select(payment => payment.Contract!)
            .GroupBy(contract => contract.Id)
            .ToDictionary(group => group.Key, group => group.First());

        if (invoicePaymentIds.Count > 0)
        {
            var invoiceContracts = await _db.ContractInvoices
                .AsNoTracking()
                .Include(link => link.Contract)
                .Where(link => invoicePaymentIds.Contains(link.InvoiceId))
                .Select(link => link.Contract)
                .ToListAsync(ct);

            foreach (var contract in invoiceContracts)
            {
                contractsById.TryAdd(contract.Id, contract);
            }
        }

        if (paymentIds.Count > 0)
        {
            var paymentContracts = await _db.ContractPayments
                .AsNoTracking()
                .Include(link => link.Contract)
                .Where(link => paymentIds.Contains(link.PaymentId))
                .Select(link => link.Contract)
                .ToListAsync(ct);

            foreach (var contract in paymentContracts)
            {
                contractsById.TryAdd(contract.Id, contract);
            }
        }

        return contractsById;
    }

    private static ClientCaseFinanceTotalsDto BuildFinanceTotals(ClientCase clientCase, IReadOnlyCollection<Payment> payments)
    {
        var totals = new ClientCaseFinanceTotalsDto
        {
            FeeAmount = clientCase.FeeAmount,
        };

        foreach (var payment in payments)
        {
            var outstanding = ResolveFinancePaymentOutstanding(payment);
            if (payment.Type == PaymentType.Income)
            {
                totals.IncomeTotal += payment.Amount;
                totals.IncomePaid += payment.PaidAmount;
                totals.IncomeOutstanding += outstanding;
                totals.IncomeCount += 1;
            }
            else
            {
                totals.ExpenseTotal += payment.Amount;
                totals.ExpensePaid += payment.PaidAmount;
                totals.ExpenseOutstanding += outstanding;
                totals.ExpenseCount += 1;
            }
        }

        totals.PlannedMargin = totals.IncomeTotal - totals.ExpenseTotal;
        totals.PaidMargin = totals.IncomePaid - totals.ExpensePaid;
        totals.OutstandingBalance = clientCase.FeeAmount > 0
            ? Math.Max(clientCase.FeeAmount - totals.IncomePaid, 0m)
            : totals.IncomeOutstanding;
        return totals;
    }

    private static ClientCaseFinanceCalendarDto BuildFinanceCalendar(IReadOnlyCollection<Payment> payments)
    {
        var today = DateTime.UtcNow.Date;
        var calendar = new ClientCaseFinanceCalendarDto();

        foreach (var payment in payments)
        {
            var outstanding = ResolveFinancePaymentOutstanding(payment);
            calendar.PaymentCount += 1;
            calendar.PlanTotal += payment.Amount;
            calendar.PaidTotal += payment.PaidAmount;
            calendar.OutstandingTotal += outstanding;

            if (payment.Status == PaymentStatus.Cancelled)
            {
                calendar.CancelledCount += 1;
                continue;
            }

            if (outstanding <= 0m || payment.Status == PaymentStatus.Completed)
            {
                calendar.PaidCount += 1;
                continue;
            }

            var plannedDate = ResolveFinancePaymentCalendarDate(payment).Date;
            if (payment.Status == PaymentStatus.Overdue || plannedDate < today)
            {
                calendar.OverdueCount += 1;
                calendar.OverdueTotal += outstanding;
                continue;
            }

            calendar.PlannedCount += 1;
            calendar.NextPaymentDate = calendar.NextPaymentDate is null || plannedDate < calendar.NextPaymentDate.Value.Date
                ? plannedDate
                : calendar.NextPaymentDate;
        }

        return calendar;
    }

    private static IReadOnlyList<ClientCaseFinanceInvoiceDto> BuildFinanceInvoices(
        IReadOnlyCollection<Payment> payments,
        IReadOnlyDictionary<int, Contract> contractsById)
    {
        return payments
            .Where(IsFinanceInvoiceCandidate)
            .GroupBy(BuildFinanceInvoiceKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => MapFinanceInvoice(group.ToList(), contractsById))
            .OrderByDescending(invoice => invoice.Date ?? DateTime.MinValue)
            .ThenBy(invoice => invoice.Number)
            .ToList();
    }

    private static bool IsFinanceInvoiceCandidate(Payment payment)
        => payment.DocumentKind == DocumentKind.Invoice
           || !string.IsNullOrWhiteSpace(payment.Account)
           || !string.IsNullOrWhiteSpace(payment.LegacyAccountNumber);

    private static string BuildFinanceInvoiceKey(Payment payment)
    {
        var number = NormalizeFinanceInvoiceNumber(payment.LegacyAccountNumber ?? payment.Account);
        var date = payment.LegacyAccountDate ?? payment.AccountDate ?? payment.Date;
        if (!string.IsNullOrWhiteSpace(number))
        {
            return $"{payment.Type}:{number}:{date:yyyy-MM-dd}";
        }

        return $"payment:{payment.Id}";
    }

    private static ClientCaseFinanceInvoiceDto MapFinanceInvoice(
        IReadOnlyCollection<Payment> group,
        IReadOnlyDictionary<int, Contract> contractsById)
    {
        var representative = group
            .OrderByDescending(payment => payment.DocumentKind == DocumentKind.Invoice)
            .ThenBy(payment => payment.Id)
            .First();
        var contract = representative.ContractId.HasValue && contractsById.TryGetValue(representative.ContractId.Value, out var directContract)
            ? directContract
            : null;

        var amount = group.Sum(payment => payment.Amount);
        var paidAmount = group.Sum(payment => payment.PaidAmount);
        var outstandingAmount = group.Sum(ResolveFinancePaymentOutstanding);

        return new ClientCaseFinanceInvoiceDto
        {
            Id = representative.Id,
            Number = ResolveFinanceInvoiceNumber(representative),
            Date = representative.LegacyAccountDate ?? representative.AccountDate ?? representative.Date,
            Amount = amount,
            PaidAmount = paidAmount,
            OutstandingAmount = outstandingAmount,
            Status = ResolveFinanceGroupStatus(group),
            WorkflowStatus = representative.InvoiceWorkflowStatus,
            ContractId = contract?.Id ?? representative.ContractId,
            ContractNumber = contract?.Number ?? representative.Contract?.Number,
        };
    }

    private static string ResolveFinanceInvoiceNumber(Payment payment)
    {
        var number = payment.LegacyAccountNumber ?? payment.Account;
        return !string.IsNullOrWhiteSpace(number)
            ? number.Trim()
            : $"#{payment.Id}";
    }

    private static string ResolveFinanceGroupStatus(IReadOnlyCollection<Payment> payments)
    {
        if (payments.Any(payment => payment.Status == PaymentStatus.Overdue))
        {
            return PaymentStatus.Overdue.ToString();
        }

        if (payments.All(payment => payment.Status == PaymentStatus.Completed))
        {
            return PaymentStatus.Completed.ToString();
        }

        if (payments.Any(payment => payment.Status == PaymentStatus.Processing))
        {
            return PaymentStatus.Processing.ToString();
        }

        return payments.First().Status.ToString();
    }

    private static ClientCaseFinancePaymentDto MapFinancePayment(Payment payment)
        => new()
        {
            Id = payment.Id,
            DocumentKind = payment.DocumentKind.ToString(),
            Type = payment.Type.ToString(),
            Amount = payment.Amount,
            PaidAmount = payment.PaidAmount,
            OutstandingAmount = ResolveFinancePaymentOutstanding(payment),
            Status = payment.Status.ToString(),
            Date = payment.Date,
            PlannedDate = payment.PlannedDate,
            PaidDate = payment.PaidDate,
            InvoiceId = payment.DocumentKind == DocumentKind.Invoice ? payment.Id : null,
            InvoiceNumber = IsFinanceInvoiceCandidate(payment) ? ResolveFinanceInvoiceNumber(payment) : null,
            InvoiceDate = IsFinanceInvoiceCandidate(payment) ? payment.LegacyAccountDate ?? payment.AccountDate ?? payment.Date : null,
            ContractId = payment.ContractId,
            ContractNumber = payment.Contract?.Number,
            PaymentSourceName = payment.PaymentSource?.Name,
            PaymentOrderNumber = payment.PaymentOrderNumber,
            PaymentDocumentDate = payment.PaymentDocumentDate,
            Description = payment.Description,
        };

    private static ClientCaseFinanceContractDto MapFinanceContract(Contract contract)
        => new()
        {
            Id = contract.Id,
            Number = contract.Number,
            Title = contract.Title,
            Date = contract.Date,
            Amount = contract.Amount,
            Status = contract.Status,
        };

    private static decimal ResolveFinancePaymentOutstanding(Payment payment)
        => payment.Amount > payment.PaidAmount
            ? Math.Round(payment.Amount - payment.PaidAmount, 2)
            : 0m;

    private static DateTime ResolveFinancePaymentSortDate(Payment payment)
        => payment.PaidDate ?? payment.LastPaymentDate ?? payment.Date;

    private static DateTime ResolveFinancePaymentCalendarDate(Payment payment)
        => payment.PlannedDate != default
            ? payment.PlannedDate
            : payment.Date;

    private static string? NormalizeFinanceInvoiceNumber(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    private async Task<ActionResult<(ClientCase ClientCase, ClientCaseWorkOrder WorkOrder)>> LoadEditableWorkOrderAsync(
        int id,
        CancellationToken ct)
    {
        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var clientCase = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var workOrder = await _db.ClientCaseWorkOrders
            .Include(item => item.Items)
            .FirstOrDefaultAsync(item => item.ClientCaseId == id, ct);
        if (workOrder is null)
        {
            return NotFound();
        }

        return (clientCase, workOrder);
    }

    private async Task<(ClientCaseWorkOrderItem? Item, string? Error)> BuildWorkOrderItemAsync(
        ClientCaseWorkOrder workOrder,
        UpsertClientCaseWorkOrderItemRequest? request,
        CancellationToken ct)
    {
        var nextSortOrder = workOrder.Items.Count == 0 ? 1 : workOrder.Items.Max(item => item.SortOrder) + 1;
        var item = new ClientCaseWorkOrderItem
        {
            ClientCaseWorkOrderId = workOrder.Id,
            SortOrder = request?.SortOrder > 0 ? request.SortOrder.Value : nextSortOrder,
        };
        var error = await ApplyWorkOrderItemAsync(item, request, ct);
        return (item, error);
    }

    private async Task<string?> ApplyWorkOrderItemAsync(
        ClientCaseWorkOrderItem item,
        UpsertClientCaseWorkOrderItemRequest? request,
        CancellationToken ct)
    {
        item.ServiceName = Truncate(NormalizeRequired(request?.ServiceName, item.ServiceName), 200);
        item.Description = Truncate(NormalizeOptional(request?.Description) ?? item.Description, 1000);
        item.DueDate = request?.DueDate ?? item.DueDate;
        item.Quantity = NormalizePositiveMoney(request?.Quantity, item.Quantity <= 0 ? 1 : item.Quantity);
        item.UnitPrice = NormalizeNonNegativeMoney(request?.UnitPrice, item.UnitPrice);
        item.TotalAmount = decimal.Round(item.Quantity * item.UnitPrice, 2, MidpointRounding.AwayFromZero);
        if (request?.SortOrder is > 0)
        {
            item.SortOrder = request.SortOrder.Value;
        }

        if (request?.ExecutorUserId is not null)
        {
            if (request.ExecutorUserId <= 0)
            {
                item.ExecutorUserId = null;
                item.ExecutorName = string.Empty;
            }
            else
            {
                var executor = await _db.Users
                    .AsNoTracking()
                    .FirstOrDefaultAsync(user => user.Id == request.ExecutorUserId.Value && user.IsActive, ct);
                if (executor is null)
                {
                    return "Исполнитель услуги не найден или неактивен.";
                }

                item.ExecutorUserId = executor.Id;
                item.ExecutorName = executor.FullName;
            }
        }
        else if (NormalizeOptional(request?.ExecutorName) is { } executorName)
        {
            item.ExecutorName = Truncate(executorName, 256);
        }

        return null;
    }

    private static void RecalculateWorkOrder(ClientCaseWorkOrder workOrder)
    {
        foreach (var item in workOrder.Items)
        {
            item.TotalAmount = decimal.Round(item.Quantity * item.UnitPrice, 2, MidpointRounding.AwayFromZero);
        }

        workOrder.TotalAmount = decimal.Round(workOrder.Items.Sum(item => item.TotalAmount), 2, MidpointRounding.AwayFromZero);
    }

    private void StampWorkOrderUpdate(ClientCaseWorkOrder workOrder)
    {
        workOrder.UpdatedAt = DateTime.UtcNow;
        workOrder.UpdatedByUserId = User.GetUserId();
        workOrder.UpdatedByUserName = User.GetDisplayName();
    }

    private void ApplyWorkOrderApprovalDecision(ClientCaseWorkOrder workOrder, string status, string? comment)
    {
        workOrder.Status = status;
        workOrder.ApprovalDecisionAt = DateTime.UtcNow;
        workOrder.ApprovalDecisionByUserId = User.GetUserId();
        workOrder.ApprovalDecisionByUserName = User.GetDisplayName();
        workOrder.ApprovalComment = Truncate(NormalizeOptional(comment) ?? string.Empty, 1000);
        StampWorkOrderUpdate(workOrder);
    }

    private static string FormatOptionalComment(string? comment)
    {
        var normalized = NormalizeOptional(comment);
        return normalized is null ? string.Empty : $" Комментарий: {normalized}.";
    }

    private static ClientCaseWorkOrderDto MapWorkOrder(ClientCaseWorkOrder workOrder)
    {
        RecalculateWorkOrder(workOrder);
        return new ClientCaseWorkOrderDto
        {
            Id = workOrder.Id,
            ClientCaseId = workOrder.ClientCaseId,
            Title = workOrder.Title,
            Status = workOrder.Status,
            StatusLabel = FormatWorkOrderStatus(workOrder.Status),
            Currency = workOrder.Currency,
            Notes = workOrder.Notes,
            TotalAmount = workOrder.TotalAmount,
            CreatedAt = workOrder.CreatedAt,
            CreatedByUserId = workOrder.CreatedByUserId,
            CreatedByUserName = workOrder.CreatedByUserName,
            UpdatedAt = workOrder.UpdatedAt,
            UpdatedByUserId = workOrder.UpdatedByUserId,
            UpdatedByUserName = workOrder.UpdatedByUserName,
            ApprovalRequestedAt = workOrder.ApprovalRequestedAt,
            ApprovalRequestedByUserId = workOrder.ApprovalRequestedByUserId,
            ApprovalRequestedByUserName = workOrder.ApprovalRequestedByUserName,
            ApprovalDecisionAt = workOrder.ApprovalDecisionAt,
            ApprovalDecisionByUserId = workOrder.ApprovalDecisionByUserId,
            ApprovalDecisionByUserName = workOrder.ApprovalDecisionByUserName,
            ApprovalComment = workOrder.ApprovalComment,
            Items = workOrder.Items
                .OrderBy(item => item.SortOrder)
                .ThenBy(item => item.Id)
                .Select(MapWorkOrderItem)
                .ToList(),
        };
    }

    private static ClientCaseWorkOrderItemDto MapWorkOrderItem(ClientCaseWorkOrderItem item)
        => new()
        {
            Id = item.Id,
            ClientCaseWorkOrderId = item.ClientCaseWorkOrderId,
            ServiceName = item.ServiceName,
            Description = item.Description,
            ExecutorUserId = item.ExecutorUserId,
            ExecutorName = item.ExecutorName,
            DueDate = item.DueDate,
            Quantity = item.Quantity,
            UnitPrice = item.UnitPrice,
            TotalAmount = item.TotalAmount,
            SortOrder = item.SortOrder,
        };

    private static string BuildWorkOrderPrintHtml(ClientCase clientCase, ClientCaseWorkOrder workOrder)
    {
        RecalculateWorkOrder(workOrder);
        var generatedAt = DateTime.UtcNow.ToString("dd.MM.yyyy HH:mm", CultureInfo.GetCultureInfo("ru-RU"));
        var rows = string.Join(Environment.NewLine, workOrder.Items
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Id)
            .Select((item, index) => $"""
              <tr>
                <td>{index + 1}</td>
                <td>
                  <strong>{Html(item.ServiceName)}</strong>
                  <div class="muted">{Html(item.Description)}</div>
                </td>
                <td>{Html(item.ExecutorName)}</td>
                <td>{Html(FormatRuDate(item.DueDate))}</td>
                <td class="num">{Html(item.Quantity.ToString("N2", CultureInfo.GetCultureInfo("ru-RU")))}</td>
                <td class="num">{Html(FormatMoney(item.UnitPrice))}</td>
                <td class="num">{Html(FormatMoney(item.TotalAmount))}</td>
              </tr>
            """));
        if (string.IsNullOrWhiteSpace(rows))
        {
            rows = """<tr><td colspan="7" class="empty">Услуги пока не добавлены.</td></tr>""";
        }

        return $$"""
<!doctype html>
<html lang="ru">
<head>
  <meta charset="utf-8" />
  <title>Заказ-наряд {{Html(workOrder.Title)}}</title>
  <style>
    :root { color-scheme: light; font-family: Inter, Arial, sans-serif; color: #0f172a; }
    body { margin: 0; padding: 32px; background: #f8fafc; }
    main { max-width: 1100px; margin: 0 auto; background: #fff; border: 1px solid #e2e8f0; border-radius: 12px; padding: 28px; }
    h1 { margin: 0; font-size: 28px; line-height: 1.2; }
    h2 { margin: 28px 0 10px; font-size: 16px; text-transform: uppercase; letter-spacing: .08em; color: #64748b; }
    .meta { margin-top: 8px; color: #64748b; font-size: 13px; }
    .grid { display: grid; grid-template-columns: repeat(3, 1fr); gap: 12px; margin-top: 20px; }
    .stat { border: 1px solid #e2e8f0; border-radius: 10px; padding: 12px; background: #f8fafc; }
    .stat span { display: block; color: #64748b; font-size: 12px; text-transform: uppercase; letter-spacing: .08em; }
    .stat strong { display: block; margin-top: 6px; font-size: 18px; }
    table { width: 100%; border-collapse: collapse; margin-top: 12px; font-size: 13px; }
    th, td { border-bottom: 1px solid #e2e8f0; padding: 10px 8px; text-align: left; vertical-align: top; }
    th { color: #475569; font-size: 11px; text-transform: uppercase; letter-spacing: .08em; background: #f8fafc; }
    .num { text-align: right; white-space: nowrap; }
    .muted { margin-top: 3px; color: #64748b; font-size: 12px; }
    .empty { text-align: center; color: #64748b; padding: 24px; }
    .total { margin-top: 18px; display: flex; justify-content: flex-end; font-size: 20px; font-weight: 700; }
    @media print { body { padding: 0; background: #fff; } main { border: 0; border-radius: 0; } }
  </style>
</head>
<body>
  <main>
    <h1>Заказ-наряд / предварительный расчет</h1>
    <div class="meta">Сформировано: {{Html(generatedAt)}}</div>
    <div class="grid">
      <div class="stat"><span>Дело</span><strong>{{Html(clientCase.Title)}}</strong><div class="muted">{{Html(clientCase.CaseNumber)}}</div></div>
      <div class="stat"><span>Клиент</span><strong>{{Html(GetOwnerName(clientCase))}}</strong></div>
      <div class="stat"><span>Ответственный</span><strong>{{Html(GetResponsibleName(clientCase))}}</strong></div>
      <div class="stat"><span>Статус</span><strong>{{Html(FormatWorkOrderStatus(workOrder.Status))}}</strong></div>
      <div class="stat"><span>Валюта</span><strong>{{Html(workOrder.Currency)}}</strong></div>
      <div class="stat"><span>Итого</span><strong>{{Html(FormatMoney(workOrder.TotalAmount))}}</strong></div>
    </div>
    <h2>{{Html(workOrder.Title)}}</h2>
    <div class="meta">{{Html(workOrder.Notes)}}</div>
    <table>
      <thead>
        <tr>
          <th>в"–</th>
          <th>Услуга</th>
          <th>Исполнитель</th>
          <th>Срок</th>
          <th class="num">Кол-во</th>
          <th class="num">Цена</th>
          <th class="num">Сумма</th>
        </tr>
      </thead>
      <tbody>
{{rows}}
      </tbody>
    </table>
    <div class="total">Итого: {{Html(FormatMoney(workOrder.TotalAmount))}}</div>
  </main>
</body>
</html>
""";
    }

    private static string BuildWorkOrderInvoiceDocumentHtml(
        ClientCase clientCase,
        ClientCaseWorkOrder workOrder,
        Payment payment,
        string number,
        DateTime invoiceDate,
        DateTime dueDate)
    {
        var generatedAt = DateTime.UtcNow.ToString("dd.MM.yyyy HH:mm", CultureInfo.GetCultureInfo("ru-RU"));
        var rows = string.Join(Environment.NewLine, payment.InvoiceLineItems
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Id)
            .Select((item, index) => $"""
              <tr>
                <td>{index + 1}</td>
                <td>{Html(item.Name)}</td>
                <td class="num">{Html(item.Quantity.ToString("N2", CultureInfo.GetCultureInfo("ru-RU")))}</td>
                <td>{Html(item.Unit)}</td>
                <td class="num">{Html(FormatMoney(item.Price))}</td>
                <td class="num">{Html(FormatMoney(item.Quantity * item.Price))}</td>
              </tr>
            """));
        if (string.IsNullOrWhiteSpace(rows))
        {
            rows = """<tr><td colspan="6" class="empty">Позиции счета пока не добавлены.</td></tr>""";
        }

        return $$"""
<!doctype html>
<html lang="ru">
<head>
  <meta charset="utf-8" />
  <title>Счет №{{Html(number)}}</title>
  <style>
    :root { color-scheme: light; font-family: Inter, Arial, sans-serif; color: #0f172a; }
    body { margin: 0; padding: 32px; background: #f8fafc; }
    main { max-width: 1000px; margin: 0 auto; background: #fff; border: 1px solid #e2e8f0; border-radius: 12px; padding: 28px; }
    h1 { margin: 0; font-size: 28px; line-height: 1.2; }
    .meta { margin-top: 8px; color: #64748b; font-size: 13px; }
    .grid { display: grid; grid-template-columns: repeat(3, 1fr); gap: 12px; margin-top: 20px; }
    .stat { border: 1px solid #e2e8f0; border-radius: 10px; padding: 12px; background: #f8fafc; }
    .stat span { display: block; color: #64748b; font-size: 12px; text-transform: uppercase; letter-spacing: .08em; }
    .stat strong { display: block; margin-top: 6px; font-size: 18px; }
    table { width: 100%; border-collapse: collapse; margin-top: 18px; font-size: 13px; }
    th, td { border-bottom: 1px solid #e2e8f0; padding: 10px 8px; text-align: left; vertical-align: top; }
    th { color: #475569; font-size: 11px; text-transform: uppercase; letter-spacing: .08em; background: #f8fafc; }
    .num { text-align: right; white-space: nowrap; }
    .empty { text-align: center; color: #64748b; padding: 24px; }
    .total { margin-top: 18px; display: flex; justify-content: flex-end; font-size: 20px; font-weight: 700; }
    @media print { body { padding: 0; background: #fff; } main { border: 0; border-radius: 0; } }
  </style>
</head>
<body>
  <main>
    <h1>Счет №{{Html(number)}}</h1>
    <div class="meta">Сформировано: {{Html(generatedAt)}}</div>
    <div class="grid">
      <div class="stat"><span>Дело</span><strong>{{Html(clientCase.Title)}}</strong><div class="meta">{{Html(clientCase.CaseNumber)}}</div></div>
      <div class="stat"><span>Клиент</span><strong>{{Html(GetOwnerName(clientCase))}}</strong></div>
      <div class="stat"><span>Основание</span><strong>{{Html(workOrder.Title)}}</strong></div>
      <div class="stat"><span>Дата счета</span><strong>{{Html(FormatRuDate(invoiceDate))}}</strong></div>
      <div class="stat"><span>Срок оплаты</span><strong>{{Html(FormatRuDate(dueDate))}}</strong></div>
      <div class="stat"><span>Сумма</span><strong>{{Html(FormatMoney(payment.Amount))}}</strong></div>
    </div>
    <table>
      <thead>
        <tr>
          <th>№</th>
          <th>Услуга</th>
          <th class="num">Кол-во</th>
          <th>Ед.</th>
          <th class="num">Цена</th>
          <th class="num">Сумма</th>
        </tr>
      </thead>
      <tbody>
{{rows}}
      </tbody>
    </table>
    <div class="total">Итого: {{Html(FormatMoney(payment.Amount))}}</div>
  </main>
</body>
</html>
""";
    }

    private static ClientCaseProceduralDeadlineDto MapProceduralDeadline(ClientCaseProceduralDeadline item)
        => new()
        {
            Id = item.Id,
            ClientCaseId = item.ClientCaseId,
            DeadlineType = item.DeadlineType,
            DeadlineTypeLabel = ResolveProceduralDeadlineTypeLabel(item.DeadlineType),
            Title = item.Title,
            Description = item.Description,
            DueAt = item.DueAt,
            AllDay = item.AllDay,
            Status = item.Status,
            StatusLabel = ResolveProceduralDeadlineStatusLabel(item.Status),
            WarningDaysBefore = item.WarningDaysBefore,
            TaskJournalItemId = item.TaskJournalItemId,
            ClientCaseDocumentId = item.ClientCaseDocumentId,
            SourceType = item.SourceType,
            SourceId = item.SourceId,
            Notes = item.Notes,
            CreatedAt = item.CreatedAt,
            CreatedByUserId = item.CreatedByUserId,
            CreatedByUserName = item.CreatedByUserName,
            UpdatedAt = item.UpdatedAt,
            UpdatedByUserId = item.UpdatedByUserId,
            UpdatedByUserName = item.UpdatedByUserName,
            ClosedAt = item.ClosedAt,
            ClosedByUserId = item.ClosedByUserId,
            ClosedByUserName = item.ClosedByUserName,
            CloseReason = item.CloseReason,
            IsOverdue = item.Status != "closed" && item.DueAt.Date < DateTime.UtcNow.Date,
            IsWarning = item.Status != "closed" && item.DueAt.Date >= DateTime.UtcNow.Date && item.DueAt.Date <= DateTime.UtcNow.Date.AddDays(Math.Max(0, item.WarningDaysBefore)),
        };

    private static ClientCaseCourtEventDto MapCourtEvent(ClientCaseCourtEvent item)
        => new()
        {
            Id = item.Id,
            ClientCaseId = item.ClientCaseId,
            EventType = item.EventType,
            EventTypeLabel = ResolveCourtEventTypeLabel(item.EventType),
            Title = item.Title,
            Description = item.Description,
            ScheduledAt = item.ScheduledAt,
            AllDay = item.AllDay,
            CourtInstance = item.CourtInstance,
            CourtInstanceLabel = ResolveCourtInstanceLabel(item.CourtInstance),
            CourtId = item.CourtId,
            CourtName = item.CourtName,
            CourtCaseNumber = item.CourtCaseNumber,
            Courtroom = item.Courtroom,
            JudgeName = item.JudgeName,
            SecretaryName = item.SecretaryName,
            MovementStatus = item.MovementStatus,
            MovementStatusLabel = ResolveCourtMovementStatusLabel(item.MovementStatus),
            ResultSummary = item.ResultSummary,
            SourceType = item.SourceType,
            ClientCaseDocumentId = item.ClientCaseDocumentId,
            ProceduralDeadlineId = item.ProceduralDeadlineId,
            CreatedAt = item.CreatedAt,
            CreatedByUserId = item.CreatedByUserId,
            CreatedByUserName = item.CreatedByUserName,
            UpdatedAt = item.UpdatedAt,
            UpdatedByUserId = item.UpdatedByUserId,
            UpdatedByUserName = item.UpdatedByUserName,
            IsOverdue = item.MovementStatus is not "held" and not "completed" and not "cancelled" && item.ScheduledAt.Date < DateTime.UtcNow.Date,
        };

    private static ClientCaseCourtEventHistoryDto MapCourtEventHistory(ClientCaseCourtEventHistory item)
        => new()
        {
            Id = item.Id,
            ClientCaseCourtEventId = item.ClientCaseCourtEventId,
            ClientCaseId = item.ClientCaseId,
            Action = item.Action,
            Summary = item.Summary,
            OccurredAt = item.OccurredAt,
            ActorUserId = item.ActorUserId,
            ActorUserName = item.ActorUserName,
        };

    private void AddCourtEventHistory(
        ClientCaseCourtEvent courtEvent,
        string action,
        string summary,
        int? actorUserId,
        string actorName)
    {
        _db.ClientCaseCourtEventHistory.Add(new ClientCaseCourtEventHistory
        {
            ClientCaseCourtEventId = courtEvent.Id,
            ClientCaseId = courtEvent.ClientCaseId,
            Action = Truncate(action, 64),
            Summary = Truncate(summary, 1000),
            OccurredAt = DateTime.UtcNow,
            ActorUserId = actorUserId,
            ActorUserName = Truncate(actorName, 256),
        });
    }

    private static ClientCaseCourtDto MapCourt(ClientCaseCourt item)
        => new()
        {
            Id = item.Id,
            Name = item.Name,
            Instance = item.Instance,
            InstanceLabel = ResolveCourtInstanceLabel(item.Instance),
            Code = item.Code,
            Url = item.Url,
            Address = item.Address,
            Contacts = item.Contacts,
            IsActive = item.IsActive,
            SortOrder = item.SortOrder,
            CreatedAt = item.CreatedAt,
            CreatedByUserId = item.CreatedByUserId,
            CreatedByUserName = item.CreatedByUserName,
            UpdatedAt = item.UpdatedAt,
            UpdatedByUserId = item.UpdatedByUserId,
            UpdatedByUserName = item.UpdatedByUserName,
        };

    private static ClientCaseCourtPartyDto MapCourtParty(ClientCaseCourtParty item)
        => new()
        {
            Id = item.Id,
            ClientCaseId = item.ClientCaseId,
            RelatedPartyId = item.RelatedPartyId,
            DisplayName = item.DisplayName,
            Side = item.Side,
            SideLabel = ResolveCourtPartySideLabel(item.Side),
            ProcessRole = item.ProcessRole,
            RepresentativeName = item.RepresentativeName,
            Notes = item.Notes,
            IsArchived = item.IsArchived,
            CreatedAt = item.CreatedAt,
            CreatedByUserId = item.CreatedByUserId,
            CreatedByUserName = item.CreatedByUserName,
            UpdatedAt = item.UpdatedAt,
            UpdatedByUserId = item.UpdatedByUserId,
            UpdatedByUserName = item.UpdatedByUserName,
        };

    private async Task<CourtPartyValidationResult> ValidateCourtPartyRequestAsync(
        int clientCaseId,
        UpsertClientCaseCourtPartyRequest? request,
        CancellationToken ct)
    {
        if (request is null)
        {
            return new("Укажите параметры стороны процесса.");
        }

        var displayName = NormalizeOptional(request.DisplayName);
        if (request.RelatedPartyId.HasValue)
        {
            var relatedParty = await _db.ClientCaseRelatedParties
                .AsNoTracking()
                .FirstOrDefaultAsync(item => item.Id == request.RelatedPartyId.Value && item.ClientCaseId == clientCaseId, ct);
            if (relatedParty is null)
            {
                return new("Связанный участник дела не найден.");
            }

            displayName ??= relatedParty.DisplayName;
        }

        if (displayName is null)
        {
            return new("Укажите наименование стороны процесса.");
        }

        return new(
            null,
            request.RelatedPartyId,
            Truncate(displayName, 300),
            NormalizeCourtPartySide(request.Side),
            Truncate(NormalizeOptional(request.ProcessRole) ?? "Участник процесса", 120),
            Truncate(NormalizeOptional(request.RepresentativeName) ?? string.Empty, 300),
            Truncate(NormalizeOptional(request.Notes) ?? string.Empty, 1000));
    }

    private static string NormalizeCourtPartySide(string? value)
    {
        var normalized = NormalizeOptional(value)?.ToLowerInvariant();
        return normalized switch
        {
            "claimant" or "plaintiff" => "claimant",
            "respondent" or "defendant" => "respondent",
            "thirdparty" or "third-party" or "third_party" => "thirdParty",
            "applicant" => "applicant",
            _ => "other",
        };
    }

    private static string ResolveCourtPartySideLabel(string value)
        => value switch
        {
            "claimant" => "Истец",
            "respondent" => "Ответчик",
            "thirdParty" => "Третье лицо",
            "applicant" => "Заявитель",
            _ => "Иная сторона",
        };

    private async Task<ClientCaseCourt?> ResolveCourtDirectoryItemForRequestAsync(int? courtId, CancellationToken ct)
    {
        if (!courtId.HasValue)
        {
            return null;
        }

        return await _db.ClientCaseCourts
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == courtId.Value && item.IsActive, ct);
    }

    private static CourtDirectoryValidationResult ValidateCourtDirectoryRequest(UpsertClientCaseCourtRequest? request)
    {
        if (request is null)
        {
            return new("Укажите параметры суда.");
        }

        var name = NormalizeOptional(request.Name);
        if (name is null)
        {
            return new("Укажите наименование суда.");
        }

        return new(
            null,
            Truncate(name, 200),
            NormalizeCourtInstance(request.Instance),
            Truncate(NormalizeOptional(request.Code) ?? string.Empty, 100),
            Truncate(NormalizeOptional(request.Url) ?? string.Empty, 1000),
            Truncate(NormalizeOptional(request.Address) ?? string.Empty, 500),
            Truncate(NormalizeOptional(request.Contacts) ?? string.Empty, 500),
            request.IsActive ?? true,
            request.SortOrder ?? 0);
    }

    private static CourtEventValidationResult ValidateCourtEventRequest(UpsertClientCaseCourtEventRequest? request, ClientCase clientCase, ClientCaseCourt? court)
    {
        if (request is null)
        {
            return new("Укажите параметры судебного события.");
        }

        if (!request.ScheduledAt.HasValue)
        {
            return new("Укажите дату судебного события.");
        }

        var eventType = NormalizeCourtEventType(request.EventType);
        var title = NormalizeOptional(request.Title) ?? ResolveCourtEventTypeLabel(eventType);
        var allDay = request.AllDay ?? eventType is "decision" or "movement";
        return new(
            null,
            eventType,
            Truncate(title, 200),
            Truncate(NormalizeOptional(request.Description) ?? string.Empty, 1000),
            NormalizeDeadlineDateTime(request.ScheduledAt.Value, allDay),
            allDay,
            NormalizeCourtInstance(request.CourtInstance ?? court?.Instance),
            Truncate(NormalizeOptional(request.CourtName) ?? NormalizeOptional(court?.Name) ?? NormalizeOptional(clientCase.CourtName) ?? string.Empty, 200),
            Truncate(NormalizeOptional(request.CourtCaseNumber) ?? NormalizeOptional(clientCase.CourtCaseNumber) ?? string.Empty, 100),
            Truncate(NormalizeOptional(request.Courtroom) ?? NormalizeOptional(clientCase.Courtroom) ?? string.Empty, 120),
            Truncate(NormalizeOptional(request.JudgeName) ?? string.Empty, 200),
            Truncate(NormalizeOptional(request.SecretaryName) ?? string.Empty, 200),
            NormalizeCourtMovementStatus(request.MovementStatus),
            Truncate(NormalizeOptional(request.ResultSummary) ?? string.Empty, 1000),
            Truncate(NormalizeOptional(request.SourceType) ?? "manual", 64));
    }

    private static string NormalizeCourtEventType(string? value)
    {
        var normalized = NormalizeOptional(value)?.ToLowerInvariant();
        return normalized switch
        {
            "claim" or "hearing" or "decision" or "postponement" or "suspension" or "movement" => normalized,
            _ => "hearing",
        };
    }

    private static string ResolveCourtEventTypeLabel(string value)
        => value switch
        {
            "claim" => "Подача иска",
            "decision" => "Судебное решение",
            "postponement" => "Перенос заседания",
            "suspension" => "Приостановление",
            "movement" => "Движение дела",
            _ => "Судебное заседание",
        };

    private static string NormalizeCourtInstance(string? value)
    {
        var normalized = NormalizeOptional(value)?.ToLowerInvariant();
        return normalized switch
        {
            "first" or "appeal" or "cassation" or "supervision" => normalized,
            _ => "first",
        };
    }

    private static string ResolveCourtInstanceLabel(string value)
        => value switch
        {
            "appeal" => "Апелляция",
            "cassation" => "Кассация",
            "supervision" => "Надзор",
            _ => "Первая инстанция",
        };

    private static string NormalizeCourtMovementStatus(string? value)
    {
        var normalized = NormalizeOptional(value)?.ToLowerInvariant();
        return normalized switch
        {
            "scheduled" or "held" or "postponed" or "cancelled" or "completed" or "suspended" => normalized,
            _ => "scheduled",
        };
    }

    private static string ResolveCourtMovementStatusLabel(string value)
        => value switch
        {
            "held" => "Проведено",
            "postponed" => "Отложено",
            "cancelled" => "Отменено",
            "completed" => "Завершено",
            "suspended" => "Приостановлено",
            _ => "Назначено",
        };

    private async Task<string?> ValidateCourtEventLinksAsync(int clientCaseId, UpsertClientCaseCourtEventRequest? request, CancellationToken ct)
    {
        if (request?.ClientCaseDocumentId is { } documentId)
        {
            var documentExists = await _db.ClientCaseDocuments
                .AsNoTracking()
                .AnyAsync(item => item.Id == documentId && item.ClientCaseId == clientCaseId, ct);
            if (!documentExists)
            {
                return "Документ судебного события не найден в этом деле.";
            }
        }

        if (request?.ProceduralDeadlineId is { } deadlineId)
        {
            var deadlineExists = await _db.ClientCaseProceduralDeadlines
                .AsNoTracking()
                .AnyAsync(item => item.Id == deadlineId && item.ClientCaseId == clientCaseId, ct);
            if (!deadlineExists)
            {
                return "Процессуальный срок судебного события не найден в этом деле.";
            }
        }

        return null;
    }

    private async Task<string?> ValidateProceduralDeadlineLinksAsync(int clientCaseId, UpsertClientCaseProceduralDeadlineRequest? request, CancellationToken ct)
    {
        if (request?.TaskJournalItemId is { } taskId)
        {
            var taskExists = await _db.TaskJournalItems
                .AsNoTracking()
                .AnyAsync(item => item.Id == taskId && item.ClientCaseId == clientCaseId, ct);
            if (!taskExists)
            {
                return "Задача процессуального срока не найдена в этом деле.";
            }
        }

        if (request?.ClientCaseDocumentId is { } documentId)
        {
            var documentExists = await _db.ClientCaseDocuments
                .AsNoTracking()
                .AnyAsync(item => item.Id == documentId && item.ClientCaseId == clientCaseId, ct);
            if (!documentExists)
            {
                return "Документ процессуального срока не найден в этом деле.";
            }
        }

        if (IsCourtEventSourceType(request?.SourceType) && request?.SourceId is { } courtEventId)
        {
            var courtEventExists = await _db.ClientCaseCourtEvents
                .AsNoTracking()
                .AnyAsync(item => item.Id == courtEventId && item.ClientCaseId == clientCaseId, ct);
            if (!courtEventExists)
            {
                return "Судебное событие процессуального срока не найдено в этом деле.";
            }
        }

        return null;
    }

    private async Task SyncCourtEventDeadlineSourceAsync(
        ClientCaseCourtEvent courtEvent,
        int? previousDeadlineId,
        int? actorUserId,
        string actorName,
        CancellationToken ct)
    {
        if (previousDeadlineId.HasValue && previousDeadlineId != courtEvent.ProceduralDeadlineId)
        {
            var previousDeadline = await _db.ClientCaseProceduralDeadlines
                .FirstOrDefaultAsync(item => item.Id == previousDeadlineId && item.SourceId == courtEvent.Id, ct);
            if (previousDeadline is not null && IsCourtEventSourceType(previousDeadline.SourceType))
            {
                previousDeadline.SourceType = "manual";
                previousDeadline.SourceId = null;
                previousDeadline.UpdatedAt = DateTime.UtcNow;
                previousDeadline.UpdatedByUserId = actorUserId;
                previousDeadline.UpdatedByUserName = Truncate(actorName, 256);
            }
        }

        if (!courtEvent.ProceduralDeadlineId.HasValue)
        {
            return;
        }

        var deadline = await _db.ClientCaseProceduralDeadlines
            .FirstOrDefaultAsync(item => item.Id == courtEvent.ProceduralDeadlineId && item.ClientCaseId == courtEvent.ClientCaseId, ct);
        if (deadline is null)
        {
            return;
        }

        deadline.SourceType = "court-event";
        deadline.SourceId = courtEvent.Id;
        deadline.ClientCaseDocumentId ??= courtEvent.ClientCaseDocumentId;
        deadline.UpdatedAt = DateTime.UtcNow;
        deadline.UpdatedByUserId = actorUserId;
        deadline.UpdatedByUserName = Truncate(actorName, 256);
    }

    private async Task SyncDeadlineCourtEventLinkAsync(
        ClientCaseProceduralDeadline deadline,
        string? previousSourceType,
        int? previousSourceId,
        int? actorUserId,
        string actorName,
        CancellationToken ct)
    {
        if (IsCourtEventSourceType(previousSourceType) && previousSourceId.HasValue && previousSourceId != deadline.SourceId)
        {
            var previousCourtEvent = await _db.ClientCaseCourtEvents
                .FirstOrDefaultAsync(item => item.Id == previousSourceId && item.ProceduralDeadlineId == deadline.Id, ct);
            if (previousCourtEvent is not null)
            {
                previousCourtEvent.ProceduralDeadlineId = null;
                previousCourtEvent.UpdatedAt = DateTime.UtcNow;
                previousCourtEvent.UpdatedByUserId = actorUserId;
                previousCourtEvent.UpdatedByUserName = Truncate(actorName, 256);
            }
        }

        if (!IsCourtEventSourceType(deadline.SourceType) || !deadline.SourceId.HasValue)
        {
            return;
        }

        var courtEvent = await _db.ClientCaseCourtEvents
            .FirstOrDefaultAsync(item => item.Id == deadline.SourceId && item.ClientCaseId == deadline.ClientCaseId, ct);
        if (courtEvent is null)
        {
            return;
        }

        courtEvent.ProceduralDeadlineId = deadline.Id;
        courtEvent.ClientCaseDocumentId ??= deadline.ClientCaseDocumentId;
        courtEvent.UpdatedAt = DateTime.UtcNow;
        courtEvent.UpdatedByUserId = actorUserId;
        courtEvent.UpdatedByUserName = Truncate(actorName, 256);
        await _db.SaveChangesAsync(ct);
    }

    private static bool IsCourtEventSourceType(string? value)
    {
        var normalized = NormalizeOptional(value);
        return string.Equals(normalized, "court-event", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "courtEvent", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "court_event", StringComparison.OrdinalIgnoreCase);
    }

    private static ProceduralDeadlineValidationResult ValidateProceduralDeadlineRequest(UpsertClientCaseProceduralDeadlineRequest? request)
    {
        if (request is null)
        {
            return new("Укажите параметры процессуального срока.");
        }

        var deadlineType = NormalizeProceduralDeadlineType(request.DeadlineType);
        if (deadlineType is null)
        {
            return new("Укажите тип процессуального срока.");
        }

        var title = NormalizeOptional(request.Title) ?? ResolveProceduralDeadlineTypeLabel(deadlineType);
        if (!request.DueAt.HasValue)
        {
            return new("Укажите дату процессуального срока.");
        }

        var allDay = request.AllDay ?? true;
        return new(
            null,
            deadlineType,
            Truncate(title, 200),
            Truncate(NormalizeOptional(request.Description) ?? string.Empty, 1000),
            NormalizeDeadlineDateTime(request.DueAt.Value, allDay),
            allDay,
            NormalizeProceduralDeadlineStatus(request.Status),
            Math.Clamp(request.WarningDaysBefore ?? 7, 0, 365),
            Truncate(NormalizeOptional(request.SourceType) ?? "manual", 64),
            Truncate(NormalizeOptional(request.Notes) ?? string.Empty, 1000));
    }

    private static DateTime NormalizeDeadlineDateTime(DateTime value, bool allDay)
    {
        var normalized = value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();
        return allDay ? normalized.Date : normalized;
    }

    private static string? NormalizeProceduralDeadlineType(string? value)
    {
        var normalized = NormalizeOptional(value)?.ToLowerInvariant();
        if (normalized is null)
        {
            return null;
        }

        return normalized.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_')
            ? Truncate(normalized, 64)
            : null;
    }

    private static string NormalizeProceduralDeadlineStatus(string? value)
    {
        var normalized = NormalizeOptional(value)?.ToLowerInvariant();
        return normalized is "active" or "moved" or "closed" or "cancelled" ? normalized : "active";
    }

    private static string ResolveProceduralDeadlineTypeLabel(string value)
        => value.ToLowerInvariant() switch
        {
            "appeal" => "Апелляция",
            "cassation" => "Кассация",
            "motion" => "Ходатайство",
            "objection" => "Возражения",
            "claim" => "Подача заявления",
            "hearing" => "Судебное заседание",
            "document" => "Документ",
            _ => value,
        };

    private static string ResolveProceduralDeadlineStatusLabel(string value)
        => value.ToLowerInvariant() switch
        {
            "active" => "Активен",
            "moved" => "Перенесен",
            "closed" => "Закрыт",
            "cancelled" => "Отменен",
            _ => value,
        };

    private async Task LinkProceduralDeadlineTaskAsync(int deadlineId, CancellationToken ct)
    {
        var deadline = await _db.ClientCaseProceduralDeadlines
            .FirstOrDefaultAsync(item => item.Id == deadlineId, ct);
        if (deadline is null || deadline.TaskJournalItemId.HasValue)
        {
            return;
        }

        var tag = $"case-deadline:entity-{deadline.Id}";
        var task = await _db.TaskJournalItems
            .Where(item => item.ClientCaseId == deadline.ClientCaseId && item.TagsJson.Contains(tag))
            .OrderByDescending(item => item.UpdatedAt ?? item.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (task is null)
        {
            return;
        }

        deadline.TaskJournalItemId = task.Id;
        deadline.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    private static string NormalizeWorkOrderStatus(string? value)
    {
        var normalized = NormalizeOptional(value)?.ToLowerInvariant();
        return normalized switch
        {
            "approved" or "согласован" or "согласовано" => "approved",
            "pending_approval" or "pending-approval" or "on_approval" or "on-approval" or "на согласовании" => "pending_approval",
            "revision_required" or "revision-required" or "returned" or "returned_for_revision" or "на доработке" or "доработка" => "revision_required",
            "invoice_created" or "invoice-created" or "счет создан" or "счёт создан" => "invoice_created",
            "cancelled" or "canceled" or "отменен" or "отменён" => "cancelled",
            _ => "draft",
        };
    }

    private static string FormatWorkOrderStatus(string? value)
        => NormalizeWorkOrderStatus(value) switch
        {
            "approved" => "Согласован",
            "pending_approval" => "На согласовании",
            "revision_required" => "На доработке",
            "invoice_created" => "Счет создан",
            "cancelled" => "Отменен",
            _ => "Черновик",
        };

    private static bool IsWorkOrderLockedForEditing(ClientCaseWorkOrder workOrder)
        => IsWorkOrderApprovalManagedStatus(workOrder.Status);

    private static bool IsWorkOrderApprovalManagedStatus(string? status)
        => NormalizeWorkOrderStatus(status) is "pending_approval" or "approved" or "invoice_created";

    private static string BuildWorkOrderLockedMessage(string? status)
        => NormalizeWorkOrderStatus(status) switch
        {
            "pending_approval" => "Заказ-наряд на согласовании: редактирование доступно после возврата на доработку.",
            "approved" => "Согласованный заказ-наряд нельзя редактировать. Создайте счет или верните расчет на доработку.",
            "invoice_created" => "По заказ-наряду уже создан счет: редактирование заблокировано.",
            _ => "Заказ-наряд недоступен для редактирования в текущем статусе.",
        };

    private async Task<string> BuildWorkOrderInvoiceNumberAsync(
        int clientCaseId,
        int workOrderId,
        DateTime invoiceDate,
        string? requestedNumber,
        CancellationToken ct)
    {
        var baseNumber = NormalizeOptional(requestedNumber) ?? $"WO-{clientCaseId}-{workOrderId}";
        var candidate = baseNumber.Length > 120 ? baseNumber[..120] : baseNumber;
        var suffix = 1;
        while (await _db.Invoices.AsNoTracking().AnyAsync(
            invoice => invoice.Number == candidate && invoice.Date.Date == invoiceDate.Date,
            ct))
        {
            suffix++;
            var suffixText = $"-{suffix}";
            var maxBaseLength = Math.Max(1, 120 - suffixText.Length);
            var truncatedBase = baseNumber.Length > maxBaseLength ? baseNumber[..maxBaseLength] : baseNumber;
            candidate = $"{truncatedBase}{suffixText}";
        }

        return candidate;
    }

    private static string NormalizeCurrency(string? value)
    {
        var normalized = NormalizeOptional(value)?.ToUpperInvariant();
        return normalized is { Length: 3 } ? normalized : "RUB";
    }

    private static decimal NormalizePositiveMoney(decimal? value, decimal fallback)
    {
        var current = value ?? fallback;
        return current <= 0 ? 1 : decimal.Round(current, 2, MidpointRounding.AwayFromZero);
    }

    private static decimal NormalizeNonNegativeMoney(decimal? value, decimal fallback)
    {
        var current = value ?? fallback;
        return current < 0 ? 0 : decimal.Round(current, 2, MidpointRounding.AwayFromZero);
    }

    private async Task<ClientCaseRelatedParty?> BuildAutoLegalEntityRelatedPartyAsync(
        int clientCaseId,
        int? ownerLegalEntityId,
        LegalEntity? legalEntity,
        DataVisibilityAccess visibility,
        int? actorUserId,
        string actorName,
        CancellationToken ct)
    {
        if (legalEntity is null
            || legalEntity.Id <= 0
            || (ownerLegalEntityId.HasValue && ownerLegalEntityId.Value == legalEntity.Id)
            || !CanSeeLegalEntity(legalEntity, visibility))
        {
            return null;
        }

        var alreadyLinked = await _db.ClientCaseRelatedParties.AnyAsync(item =>
            item.ClientCaseId == clientCaseId &&
            item.SourceType == RelatedPartySourceLegalEntity &&
            !item.IsArchived &&
            item.LegalEntityId == legalEntity.Id, ct);
        if (alreadyLinked)
        {
            return null;
        }

        return new ClientCaseRelatedParty
        {
            ClientCaseId = clientCaseId,
            SourceType = RelatedPartySourceLegalEntity,
            LegalEntityId = legalEntity.Id,
            DisplayName = !string.IsNullOrWhiteSpace(legalEntity.ShortName) ? legalEntity.ShortName : legalEntity.FullName ?? $"Юрлицо #{legalEntity.Id}",
            Role = "Организация участника",
            OrganizationName = legalEntity.FullName ?? string.Empty,
            Phone = legalEntity.Phone ?? string.Empty,
            Email = legalEntity.Email ?? string.Empty,
            Notes = !string.IsNullOrWhiteSpace(legalEntity.Director) ? $"Руководитель: {legalEntity.Director.Trim()}" : string.Empty,
            CreatedByUserId = actorUserId,
            CreatedByUserName = actorName,
        };
    }

    private static bool CanSeeLegalEntity(LegalEntity legalEntity, DataVisibilityAccess visibility)
        => visibility.SeesAll
            || (legalEntity.ResponsibleUserId.HasValue && visibility.VisibleUserIds.Contains(legalEntity.ResponsibleUserId.Value));

    private static void RestoreRelatedParty(ClientCaseRelatedParty party)
    {
        party.IsArchived = false;
        party.ArchivedAt = null;
        party.ArchivedByUserId = null;
        party.ArchivedByUserName = string.Empty;
        party.ArchiveReason = string.Empty;
    }

    private static ClientCaseRelatedPartyDto MapRelatedParty(ClientCaseRelatedParty party)
        => new()
        {
            Id = party.Id,
            ClientCaseId = party.ClientCaseId,
            SourceType = party.SourceType,
            SourceId = ResolveRelatedPartySourceId(party),
            ClientId = party.ClientId,
            ExternalContactId = party.ExternalContactId,
            LegalEntityId = party.LegalEntityId,
            DisplayName = party.DisplayName,
            Role = party.Role,
            OrganizationName = party.OrganizationName,
            Phone = party.Phone,
            Email = party.Email,
            Notes = party.Notes,
            CreatedAt = party.CreatedAt,
            CreatedByUserId = party.CreatedByUserId,
            CreatedByUserName = party.CreatedByUserName,
            IsArchived = party.IsArchived,
            ArchivedAt = party.ArchivedAt,
            ArchivedByUserId = party.ArchivedByUserId,
            ArchivedByUserName = party.ArchivedByUserName,
            ArchiveReason = party.ArchiveReason,
        };

    private static int? ResolveRelatedPartySourceId(ClientCaseRelatedParty party)
        => party.SourceType switch
        {
            RelatedPartySourceClient => party.ClientId,
            RelatedPartySourceExternalContact => party.ExternalContactId,
            RelatedPartySourceLegalEntity => party.LegalEntityId,
            _ => party.ClientId ?? party.ExternalContactId ?? party.LegalEntityId,
        };

    private static string? NormalizeRelatedPartySourceType(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        return normalized.ToLowerInvariant() switch
        {
            "client" or "person" or "physical" => RelatedPartySourceClient,
            "external" or "externalcontact" or "contact" => RelatedPartySourceExternalContact,
            "legalentity" or "legal" or "organization" or "company" => RelatedPartySourceLegalEntity,
            _ => null,
        };
    }

    private static string NormalizeRequired(string? value, string fallback)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    private static string? NormalizeDocumentKey(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        if (normalized.Length > 64)
        {
            return null;
        }

        return normalized.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_')
            ? normalized
            : null;
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength].TrimEnd();

    private static string NormalizeHistoryText(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousWasWhitespace = false;
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!previousWasWhitespace)
                {
                    builder.Append(' ');
                    previousWasWhitespace = true;
                }

                continue;
            }

            builder.Append(ch);
            previousWasWhitespace = false;
        }

        return builder.ToString().Trim();
    }

    private static void AppendCaseEvent(ClientCase clientCase, string eventText)
    {
        var line = $"{DateTime.UtcNow:dd.MM.yyyy HH:mm}: {eventText}";
        var existing = clientCase.EventHistory?.Trim();
        var next = string.IsNullOrWhiteSpace(existing)
            ? line
            : $"{existing}{Environment.NewLine}{line}";

        clientCase.EventHistory = next.Length <= 4000
            ? next
            : next[^4000..].TrimStart();
    }

    private static IReadOnlyList<string> SplitCaseHistory(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value
                .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static line => !string.IsNullOrWhiteSpace(line))
                .ToList();

    private static bool IsCourtSyncHistoryLine(string line)
        => line.Contains("проверка судебного источника", StringComparison.OrdinalIgnoreCase)
            || line.Contains("судебный источник", StringComparison.OrdinalIgnoreCase)
            || line.Contains("судебного источника", StringComparison.OrdinalIgnoreCase);

    private static ClientCaseTimelineEntryDto MapTimelineEntry(int clientCaseId, string line, int index)
    {
        var occurredAt = TryParseCaseHistoryTimestamp(line, out var parsed)
            ? parsed
            : (DateTime?)null;
        var text = line.Length > 18 && line[16] == ':' && line[17] == ' '
            ? line[18..].Trim()
            : line.Trim();

        return new ClientCaseTimelineEntryDto
        {
            Id = index + 1,
            ClientCaseId = clientCaseId,
            OccurredAt = occurredAt,
            Text = text,
        };
    }

    private static IReadOnlyList<ClientCaseConversationEntryDto> MapCaseConversation(int clientCaseId, string? eventHistory)
        => SplitCaseHistory(eventHistory)
            .Select((line, index) => MapConversationEntry(clientCaseId, line, index))
            .Where(static entry => entry is not null)
            .Select(static entry => entry!)
            .OrderByDescending(item => item.OccurredAt ?? DateTime.MinValue)
            .ThenByDescending(item => item.Id)
            .ToList();

    private static ClientCaseConversationEntryDto? MapConversationEntry(int clientCaseId, string line, int index)
    {
        var timelineEntry = MapTimelineEntry(clientCaseId, line, index);
        var text = timelineEntry.Text.Trim();
        var kind = "message";
        if (text.StartsWith(CaseConversationPrefix, StringComparison.OrdinalIgnoreCase))
        {
            text = text[CaseConversationPrefix.Length..].Trim();
        }
        else if (text.StartsWith(CaseHistoryCommentPrefix, StringComparison.OrdinalIgnoreCase))
        {
            text = text[CaseHistoryCommentPrefix.Length..].Trim();
            kind = "comment";
        }
        else
        {
            return null;
        }

        var authorName = ExtractCaseHistoryMarker(text, CaseHistoryAuthorMarker);
        var authorIndex = text.LastIndexOf(CaseHistoryAuthorMarker, StringComparison.OrdinalIgnoreCase);
        if (authorIndex >= 0)
        {
            text = text[..authorIndex].Trim();
        }

        text = text.TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return new ClientCaseConversationEntryDto
        {
            Id = index + 1,
            ClientCaseId = clientCaseId,
            OccurredAt = timelineEntry.OccurredAt,
            Text = text,
            AuthorName = authorName ?? string.Empty,
            Kind = kind,
        };
    }

    private static ClientCaseCourtSyncLogDto MapCourtSyncLog(int clientCaseId, string line, int index)
    {
        var occurredAt = TryParseCaseHistoryTimestamp(line, out var parsed)
            ? parsed
            : (DateTime?)null;
        var message = line.Length > 18 && line[16] == ':' && line[17] == ' '
            ? line[18..].Trim()
            : line.Trim();

        var actorName = ExtractCaseHistoryMarker(message, "Инициатор:");
        var status = message.Contains("не выполнена", StringComparison.OrdinalIgnoreCase)
            ? message.Contains("не указан номер дела", StringComparison.OrdinalIgnoreCase)
                ? "binding-required"
                : "external-sync-error"
            : message.Contains("обновлен", StringComparison.OrdinalIgnoreCase)
                || message.Contains("новых изменений не найдено", StringComparison.OrdinalIgnoreCase)
                    ? "external-sync-completed"
                    : message.Contains("зафиксирована", StringComparison.OrdinalIgnoreCase)
                        ? "manual-check-recorded"
                        : "history";

        return new ClientCaseCourtSyncLogDto
        {
            Id = index + 1,
            ClientCaseId = clientCaseId,
            Status = status,
            Message = message,
            OccurredAt = occurredAt,
            ActorName = actorName,
        };
    }

    private static bool TryParseCaseHistoryTimestamp(string line, out DateTime timestamp)
    {
        timestamp = default;
        return line.Length >= 16
            && DateTime.TryParseExact(
                line[..16],
                "dd.MM.yyyy HH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out timestamp);
    }

    private static string? ExtractCaseHistoryMarker(string message, string marker)
    {
        var markerIndex = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return null;
        }

        var start = markerIndex + marker.Length;
        var value = message[start..].Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string SanitizeFileName(string? value)
    {
        var fileName = Path.GetFileName(value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "file";
        }

        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(invalidChar, '_');
        }

        return fileName.Length <= 260 ? fileName : fileName[^260..];
    }

    private static string BuildStoredFileName(string documentKey, int version, string originalFileName)
    {
        var extension = Path.GetExtension(originalFileName);
        if (extension.Length > 24)
        {
            extension = string.Empty;
        }

        return $"{documentKey}-v{version}-{Guid.NewGuid():N}{extension}";
    }

    private static bool IsFinishedCaseStatus(ClientCaseStatus status)
        => status is ClientCaseStatus.Closed or ClientCaseStatus.Lost;

    private static string FormatCaseStatus(ClientCaseStatus status) => status switch
    {
        ClientCaseStatus.Open => "В работе",
        ClientCaseStatus.OnHold => "Приостановлено",
        ClientCaseStatus.Closed => "Закрыто",
        ClientCaseStatus.Lost => "Проиграно",
        _ => status.ToString(),
    };

    private static string FormatCaseResponsible(User? user)
        => string.IsNullOrWhiteSpace(user?.FullName)
            ? string.IsNullOrWhiteSpace(user?.Email) ? "Ответственный владельца" : user.Email
            : user.FullName;

    private static string FormatCaseTextValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? "не указано" : value.Trim();

    private static string FormatCasePriority(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "low" => "Низкий",
        "normal" => "Обычный",
        "high" => "Высокий",
        "urgent" => "Критический",
        _ => FormatCaseTextValue(value),
    };

    private static string BuildBulkUpdateAuditReason(
        BulkUpdateCasesRequest request,
        User? nextResponsible,
        string? nextStage,
        string? nextPriority,
        int changedCasesCount)
    {
        var targets = new List<string>();

        if (request.Status is not null)
        {
            targets.Add($"статус: {FormatCaseStatus(request.Status.Value)}");
        }

        if (request.ResponsibleUserId.HasValue)
        {
            targets.Add($"ответственный: {FormatCaseResponsible(nextResponsible)}");
        }

        if (request.IsHiddenFromExecutor.HasValue)
        {
            targets.Add(request.IsHiddenFromExecutor.Value
                ? "доступ: скрыто от исполнителя"
                : "доступ: открыто исполнителю");
        }

        if (request.InactivityRemindersEnabled.HasValue)
        {
            targets.Add(request.InactivityRemindersEnabled.Value
                ? "автоконтроль: включен"
                : "автоконтроль: выключен");
        }

        if (nextStage is not null)
        {
            targets.Add($"стадия: {nextStage}");
        }

        if (nextPriority is not null)
        {
            targets.Add($"приоритет: {FormatCasePriority(nextPriority)}");
        }

        var summary = targets.Count == 0 ? "поля не указаны" : string.Join("; ", targets);
        return $"Массовое изменение дел ({changedCasesCount}): {summary}";
    }

    public sealed class BulkUpdateCasesRequest
    {
        public int[]? Ids { get; set; }
        public ClientCaseStatus? Status { get; set; }
        public int? ResponsibleUserId { get; set; }
        public string? Stage { get; set; }
        public string? Priority { get; set; }
        public bool? IsHiddenFromExecutor { get; set; }
        public bool? InactivityRemindersEnabled { get; set; }
    }

    public sealed class RequestCaseAccessRequest
    {
        public string? Reason { get; set; }
    }

    public sealed class ResolveCaseAccessRequest
    {
        public bool Grant { get; set; }
        public string? Comment { get; set; }
    }

    public sealed class ClientCaseAccessRequestDto
    {
        public int ClientCaseId { get; set; }
        public DateTime RequestedAt { get; set; }
        public int RequestedByUserId { get; set; }
        public string RequestedByUserName { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public bool IsHiddenFromExecutor { get; set; }
    }

    public sealed class ClientCaseAccessRequestListItemDto
    {
        public int ClientCaseId { get; set; }
        public string CaseNumber { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string OwnerName { get; set; } = string.Empty;
        public int? ResponsibleUserId { get; set; }
        public string ResponsibleName { get; set; } = string.Empty;
        public DateTime RequestedAt { get; set; }
        public int? RequestedByUserId { get; set; }
        public string RequestedByUserName { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public bool IsHiddenFromExecutor { get; set; }
    }

    public sealed class ClientCaseAccessRequestResolutionDto
    {
        public int ClientCaseId { get; set; }
        public bool Granted { get; set; }
        public DateTime ResolvedAt { get; set; }
        public string ResolvedByUserName { get; set; } = string.Empty;
        public bool IsHiddenFromExecutor { get; set; }
    }

    public sealed class MarkClientCaseActivityRequest
    {
        public bool Confirmed { get; set; }
    }

    public sealed class AddClientCaseHistoryEntryRequest
    {
        public string? Text { get; set; }
    }

    public sealed class AddClientCaseConversationEntryRequest
    {
        public string? Text { get; set; }
    }

    public sealed class UpsertClientCaseRelatedPartyRequest
    {
        public string? SourceType { get; set; }
        public int SourceId { get; set; }
        public string? Role { get; set; }
        public string? Notes { get; set; }
    }

    public sealed class UpsertClientCaseWorkOrderRequest
    {
        public string? Title { get; set; }
        public string? Status { get; set; }
        public string? Currency { get; set; }
        public string? Notes { get; set; }
    }

    public sealed class UpsertClientCaseWorkOrderItemRequest
    {
        public string? ServiceName { get; set; }
        public string? Description { get; set; }
        public int? ExecutorUserId { get; set; }
        public string? ExecutorName { get; set; }
        public DateTime? DueDate { get; set; }
        public decimal? Quantity { get; set; }
        public decimal? UnitPrice { get; set; }
        public int? SortOrder { get; set; }
    }

    public sealed class CreateClientCaseWorkOrderInvoiceRequest
    {
        public string? Number { get; set; }
        public DateTime? Date { get; set; }
        public DateTime? DueDate { get; set; }
        public string? Description { get; set; }
    }

    public sealed class ClientCaseWorkOrderApprovalRequest
    {
        public string? Comment { get; set; }
    }

    public sealed class ClientCaseWorkOrderInvoiceDto
    {
        public int PaymentId { get; set; }
        public int? InvoiceEntityId { get; set; }
        public string Number { get; set; } = string.Empty;
        public DateTime Date { get; set; }
        public DateTime DueDate { get; set; }
        public decimal Amount { get; set; }
        public int? DocumentId { get; set; }
        public ClientCaseWorkOrderDto WorkOrder { get; set; } = new();
    }

    public sealed class ClientCaseWorkOrderDto
    {
        public int? Id { get; set; }
        public int ClientCaseId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string StatusLabel { get; set; } = string.Empty;
        public string Currency { get; set; } = "RUB";
        public string Notes { get; set; } = string.Empty;
        public decimal TotalAmount { get; set; }
        public DateTime? CreatedAt { get; set; }
        public int? CreatedByUserId { get; set; }
        public string CreatedByUserName { get; set; } = string.Empty;
        public DateTime? UpdatedAt { get; set; }
        public int? UpdatedByUserId { get; set; }
        public string UpdatedByUserName { get; set; } = string.Empty;
        public DateTime? ApprovalRequestedAt { get; set; }
        public int? ApprovalRequestedByUserId { get; set; }
        public string ApprovalRequestedByUserName { get; set; } = string.Empty;
        public DateTime? ApprovalDecisionAt { get; set; }
        public int? ApprovalDecisionByUserId { get; set; }
        public string ApprovalDecisionByUserName { get; set; } = string.Empty;
        public string ApprovalComment { get; set; } = string.Empty;
        public IReadOnlyList<ClientCaseWorkOrderItemDto> Items { get; set; } = [];

        public static ClientCaseWorkOrderDto Empty(int clientCaseId) => new()
        {
            ClientCaseId = clientCaseId,
            Title = "Предварительный расчет",
            Status = "draft",
            StatusLabel = "Черновик",
            Currency = "RUB",
            Items = [],
        };
    }

    public sealed class ClientCaseWorkOrderItemDto
    {
        public int Id { get; set; }
        public int ClientCaseWorkOrderId { get; set; }
        public string ServiceName { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public int? ExecutorUserId { get; set; }
        public string ExecutorName { get; set; } = string.Empty;
        public DateTime? DueDate { get; set; }
        public decimal Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal TotalAmount { get; set; }
        public int SortOrder { get; set; }
    }

    public sealed class ClientCaseProceduralDeadlineDto
    {
        public int Id { get; set; }
        public int ClientCaseId { get; set; }
        public string DeadlineType { get; set; } = string.Empty;
        public string DeadlineTypeLabel { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public DateTime DueAt { get; set; }
        public bool AllDay { get; set; }
        public string Status { get; set; } = string.Empty;
        public string StatusLabel { get; set; } = string.Empty;
        public int WarningDaysBefore { get; set; }
        public int? TaskJournalItemId { get; set; }
        public int? ClientCaseDocumentId { get; set; }
        public string SourceType { get; set; } = string.Empty;
        public int? SourceId { get; set; }
        public string Notes { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public int? CreatedByUserId { get; set; }
        public string CreatedByUserName { get; set; } = string.Empty;
        public DateTime? UpdatedAt { get; set; }
        public int? UpdatedByUserId { get; set; }
        public string UpdatedByUserName { get; set; } = string.Empty;
        public DateTime? ClosedAt { get; set; }
        public int? ClosedByUserId { get; set; }
        public string ClosedByUserName { get; set; } = string.Empty;
        public string CloseReason { get; set; } = string.Empty;
        public bool IsOverdue { get; set; }
        public bool IsWarning { get; set; }
    }

    public sealed class UpsertClientCaseProceduralDeadlineRequest
    {
        public string? DeadlineType { get; set; }
        public string? Title { get; set; }
        public string? Description { get; set; }
        public DateTime? DueAt { get; set; }
        public bool? AllDay { get; set; }
        public string? Status { get; set; }
        public int? WarningDaysBefore { get; set; }
        public int? TaskJournalItemId { get; set; }
        public int? ClientCaseDocumentId { get; set; }
        public string? SourceType { get; set; }
        public int? SourceId { get; set; }
        public string? Notes { get; set; }
        public string? CloseReason { get; set; }
    }

    public sealed class CloseClientCaseProceduralDeadlineRequest
    {
        public string? Reason { get; set; }
    }

    public sealed class ClientCaseCourtDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Instance { get; set; } = string.Empty;
        public string InstanceLabel { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public string Contacts { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public int SortOrder { get; set; }
        public DateTime CreatedAt { get; set; }
        public int? CreatedByUserId { get; set; }
        public string CreatedByUserName { get; set; } = string.Empty;
        public DateTime? UpdatedAt { get; set; }
        public int? UpdatedByUserId { get; set; }
        public string UpdatedByUserName { get; set; } = string.Empty;
    }

    public sealed class UpsertClientCaseCourtRequest
    {
        public string? Name { get; set; }
        public string? Instance { get; set; }
        public string? Code { get; set; }
        public string? Url { get; set; }
        public string? Address { get; set; }
        public string? Contacts { get; set; }
        public bool? IsActive { get; set; }
        public int? SortOrder { get; set; }
    }

    public sealed class ClientCaseCourtEventDto
    {
        public int Id { get; set; }
        public int ClientCaseId { get; set; }
        public string EventType { get; set; } = string.Empty;
        public string EventTypeLabel { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public DateTime ScheduledAt { get; set; }
        public bool AllDay { get; set; }
        public string CourtInstance { get; set; } = string.Empty;
        public string CourtInstanceLabel { get; set; } = string.Empty;
        public int? CourtId { get; set; }
        public string CourtName { get; set; } = string.Empty;
        public string CourtCaseNumber { get; set; } = string.Empty;
        public string Courtroom { get; set; } = string.Empty;
        public string JudgeName { get; set; } = string.Empty;
        public string SecretaryName { get; set; } = string.Empty;
        public string MovementStatus { get; set; } = string.Empty;
        public string MovementStatusLabel { get; set; } = string.Empty;
        public string ResultSummary { get; set; } = string.Empty;
        public string SourceType { get; set; } = string.Empty;
        public int? ClientCaseDocumentId { get; set; }
        public int? ProceduralDeadlineId { get; set; }
        public DateTime CreatedAt { get; set; }
        public int? CreatedByUserId { get; set; }
        public string CreatedByUserName { get; set; } = string.Empty;
        public DateTime? UpdatedAt { get; set; }
        public int? UpdatedByUserId { get; set; }
        public string UpdatedByUserName { get; set; } = string.Empty;
        public bool IsOverdue { get; set; }
    }

    public sealed class UpsertClientCaseCourtEventRequest
    {
        public string? EventType { get; set; }
        public string? Title { get; set; }
        public string? Description { get; set; }
        public DateTime? ScheduledAt { get; set; }
        public bool? AllDay { get; set; }
        public string? CourtInstance { get; set; }
        public int? CourtId { get; set; }
        public string? CourtName { get; set; }
        public string? CourtCaseNumber { get; set; }
        public string? Courtroom { get; set; }
        public string? JudgeName { get; set; }
        public string? SecretaryName { get; set; }
        public string? MovementStatus { get; set; }
        public string? ResultSummary { get; set; }
        public string? SourceType { get; set; }
        public int? ClientCaseDocumentId { get; set; }
        public int? ProceduralDeadlineId { get; set; }
    }

    public sealed class ClientCaseCourtEventHistoryDto
    {
        public int Id { get; set; }
        public int ClientCaseCourtEventId { get; set; }
        public int ClientCaseId { get; set; }
        public string Action { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public DateTime OccurredAt { get; set; }
        public int? ActorUserId { get; set; }
        public string ActorUserName { get; set; } = string.Empty;
    }

    public sealed class ClientCaseCourtPartyDto
    {
        public int Id { get; set; }
        public int ClientCaseId { get; set; }
        public int? RelatedPartyId { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public string Side { get; set; } = string.Empty;
        public string SideLabel { get; set; } = string.Empty;
        public string ProcessRole { get; set; } = string.Empty;
        public string RepresentativeName { get; set; } = string.Empty;
        public string Notes { get; set; } = string.Empty;
        public bool IsArchived { get; set; }
        public DateTime CreatedAt { get; set; }
        public int? CreatedByUserId { get; set; }
        public string CreatedByUserName { get; set; } = string.Empty;
        public DateTime? UpdatedAt { get; set; }
        public int? UpdatedByUserId { get; set; }
        public string UpdatedByUserName { get; set; } = string.Empty;
    }

    public sealed class UpsertClientCaseCourtPartyRequest
    {
        public int? RelatedPartyId { get; set; }
        public string? DisplayName { get; set; }
        public string? Side { get; set; }
        public string? ProcessRole { get; set; }
        public string? RepresentativeName { get; set; }
        public string? Notes { get; set; }
    }

    public sealed class ProceduralDeadlineCalculationDto
    {
        public DateTime? DeadlineAt { get; set; }
        public string Rule { get; set; } = string.Empty;
        public int MonthsToAdd { get; set; }
        public int DaysToAdd { get; set; }
        public bool RollForwardToWorkingDay { get; set; }
    }

    public sealed class CaseCalendarEventDto
    {
        public string Id { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string SourceType { get; set; } = string.Empty;
        public int? SourceId { get; set; }
        public string Title { get; set; } = string.Empty;
        public DateTime StartAt { get; set; }
        public DateTime EndAt { get; set; }
        public bool AllDay { get; set; }
        public int ClientCaseId { get; set; }
        public string CaseTitle { get; set; } = string.Empty;
        public string? ClientName { get; set; }
        public string? LegalEntityName { get; set; }
        public int? ResponsibleUserId { get; set; }
        public string ResponsibleName { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Priority { get; set; } = string.Empty;
        public string? DeadlineType { get; set; }
        public bool IsOverdue { get; set; }
        public bool IsWarning { get; set; }
        public string? CourtName { get; set; }
        public string? CourtCaseNumber { get; set; }
    }

    public sealed class CaseCalendarSummaryDto
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }
        public int TotalEventCount { get; set; }
        public int OverdueCount { get; set; }
        public int WarningCount { get; set; }
        public int CourtEventCount { get; set; }
        public int ProceduralDeadlineCount { get; set; }
        public int CaseDeadlineCount { get; set; }
        public int TaskCount { get; set; }
        public CaseCalendarEventDto? NearestEvent { get; set; }
    }

    public sealed class ClientCaseActivityDto
    {
        public DateTime? LastViewedAt { get; set; }
        public int? LastViewedByUserId { get; set; }
        public string LastViewedByUserName { get; set; } = string.Empty;
        public DateTime? ActivityConfirmedAt { get; set; }
        public int? ActivityConfirmedByUserId { get; set; }
        public string ActivityConfirmedByUserName { get; set; } = string.Empty;
        public bool InactivityRemindersEnabled { get; set; }
        public string EventHistory { get; set; } = string.Empty;
    }

    public sealed class ClientCaseTimelineEntryDto
    {
        public int Id { get; set; }
        public int ClientCaseId { get; set; }
        public DateTime? OccurredAt { get; set; }
        public string Text { get; set; } = string.Empty;
    }

    public sealed class ClientCaseConversationEntryDto
    {
        public int Id { get; set; }
        public int ClientCaseId { get; set; }
        public DateTime? OccurredAt { get; set; }
        public string Text { get; set; } = string.Empty;
        public string AuthorName { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
    }

    public sealed class ClientCaseSummaryDto
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string CaseNumber { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string StatusLabel { get; set; } = string.Empty;
        public string Priority { get; set; } = string.Empty;
        public string PriorityLabel { get; set; } = string.Empty;
        public string Stage { get; set; } = string.Empty;
        public string OwnerName { get; set; } = string.Empty;
        public int? ResponsibleUserId { get; set; }
        public string ResponsibleName { get; set; } = string.Empty;
        public DateTime? StartDate { get; set; }
        public DateTime? DueDate { get; set; }
        public DateTime? NextHearingAt { get; set; }
        public int OpenTaskCount { get; set; }
        public int OverdueTaskCount { get; set; }
        public int CompletedTaskCount { get; set; }
        public int TaskProgressPercent { get; set; }
        public decimal? FeeAmount { get; set; }
        public decimal PaidAmount { get; set; }
        public decimal DebtAmount { get; set; }
        public DateTime? LastTaskAt { get; set; }
        public DateTime? LastViewedAt { get; set; }
    }

    public sealed class ClientCaseFinanceDetailDto
    {
        public int CaseId { get; set; }
        public string CaseTitle { get; set; } = string.Empty;
        public string OwnerName { get; set; } = string.Empty;
        public ClientCaseFinanceTotalsDto Totals { get; set; } = new();
        public ClientCaseFinanceCalendarDto Calendar { get; set; } = new();
        public IReadOnlyList<ClientCaseFinanceInvoiceDto> Invoices { get; set; } = [];
        public IReadOnlyList<ClientCaseFinancePaymentDto> Payments { get; set; } = [];
        public IReadOnlyList<ClientCaseFinanceContractDto> Contracts { get; set; } = [];
    }

    public sealed class ClientCaseFinanceTotalsDto
    {
        public decimal FeeAmount { get; set; }
        public decimal IncomeTotal { get; set; }
        public decimal IncomePaid { get; set; }
        public decimal IncomeOutstanding { get; set; }
        public int IncomeCount { get; set; }
        public decimal ExpenseTotal { get; set; }
        public decimal ExpensePaid { get; set; }
        public decimal ExpenseOutstanding { get; set; }
        public int ExpenseCount { get; set; }
        public decimal PlannedMargin { get; set; }
        public decimal PaidMargin { get; set; }
        public decimal OutstandingBalance { get; set; }
    }

    public sealed class ClientCaseFinanceCalendarDto
    {
        public int PaymentCount { get; set; }
        public decimal PlanTotal { get; set; }
        public decimal PaidTotal { get; set; }
        public decimal OutstandingTotal { get; set; }
        public decimal OverdueTotal { get; set; }
        public int PaidCount { get; set; }
        public int PlannedCount { get; set; }
        public int OverdueCount { get; set; }
        public int CancelledCount { get; set; }
        public DateTime? NextPaymentDate { get; set; }
    }

    public sealed class ClientCaseFinanceInvoiceDto
    {
        public int Id { get; set; }
        public string Number { get; set; } = string.Empty;
        public DateTime? Date { get; set; }
        public decimal Amount { get; set; }
        public decimal PaidAmount { get; set; }
        public decimal OutstandingAmount { get; set; }
        public string Status { get; set; } = string.Empty;
        public string WorkflowStatus { get; set; } = string.Empty;
        public int? ContractId { get; set; }
        public string? ContractNumber { get; set; }
    }

    public sealed class ClientCaseFinancePaymentDto
    {
        public int Id { get; set; }
        public string DocumentKind { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public decimal PaidAmount { get; set; }
        public decimal OutstandingAmount { get; set; }
        public string Status { get; set; } = string.Empty;
        public DateTime Date { get; set; }
        public DateTime PlannedDate { get; set; }
        public DateTime? PaidDate { get; set; }
        public int? InvoiceId { get; set; }
        public string? InvoiceNumber { get; set; }
        public DateTime? InvoiceDate { get; set; }
        public int? ContractId { get; set; }
        public string? ContractNumber { get; set; }
        public string? PaymentSourceName { get; set; }
        public string? PaymentOrderNumber { get; set; }
        public DateTime? PaymentDocumentDate { get; set; }
        public string Description { get; set; } = string.Empty;
    }

    public sealed class ClientCaseFinanceContractDto
    {
        public int Id { get; set; }
        public string Number { get; set; } = string.Empty;
        public string? Title { get; set; }
        public DateTime Date { get; set; }
        public decimal? Amount { get; set; }
        public string Status { get; set; } = string.Empty;
    }

    public sealed class ClientCaseCourtSyncRunDto
    {
        public int ClientCaseId { get; set; }
        public string Status { get; set; } = string.Empty;
        public bool HasCourtBinding { get; set; }
        public string Message { get; set; } = string.Empty;
        public DateTime CheckedAt { get; set; }
        public int? CheckedByUserId { get; set; }
        public string CheckedByUserName { get; set; } = string.Empty;
        public string CourtName { get; set; } = string.Empty;
        public string CourtCaseNumber { get; set; } = string.Empty;
        public string CourtUrl { get; set; } = string.Empty;
        public bool SyncedFromExternalSource { get; set; }
        public IReadOnlyList<string> UpdatedFields { get; set; } = [];
        public int CreatedCourtEvents { get; set; }
        public int UpdatedCourtEvents { get; set; }
        public DateTime? NextHearingAt { get; set; }
    }

    public sealed class ClientCaseCourtSyncLogDto
    {
        public int Id { get; set; }
        public int ClientCaseId { get; set; }
        public string Status { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public DateTime? OccurredAt { get; set; }
        public string? ActorName { get; set; }
    }

    public sealed class UpdateCaseDocumentRequest
    {
        public string? DocumentType { get; set; }
        public string? Status { get; set; }
    }

    public sealed class GenerateCaseDocumentTemplateRequest
    {
        public string? TemplateKey { get; set; }
        public string? DocumentType { get; set; }
        public string? Status { get; set; }
    }

    public sealed class CaseDocumentTemplateDefinitionDto
    {
        public string Key { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string DocumentType { get; set; } = string.Empty;
        public string Heading { get; set; } = string.Empty;
        public List<string> CaseTypes { get; set; } = new();
        public string? Description { get; set; }
        public string? Content { get; set; }
        public int Version { get; set; } = 1;
        public bool IsActive { get; set; } = true;
        public DateTime? UpdatedAt { get; set; }
    }

    public sealed class CaseDocumentTemplateTokenDto
    {
        public string Key { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Placeholder { get; set; } = string.Empty;
    }

    public sealed class UpsertCaseDocumentTemplateRequest
    {
        public string? Key { get; set; }
        public string? Label { get; set; }
        public string? Name { get; set; }
        public string? DocumentType { get; set; }
        public string? Heading { get; set; }
        public List<string>? CaseTypes { get; set; }
        public string? Description { get; set; }
        public string? Content { get; set; }
        public string? Html { get; set; }
        public bool? IsActive { get; set; }
    }

    public sealed class RenderCaseDocumentTemplateRequest
    {
        public int ClientCaseId { get; set; }
        public string? DocumentType { get; set; }
    }

    public sealed class RenderedCaseDocumentTemplateDto
    {
        public string TemplateKey { get; set; } = string.Empty;
        public string TemplateLabel { get; set; } = string.Empty;
        public string DocumentType { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string Html { get; set; } = string.Empty;
    }

    private sealed class CaseDocumentTemplateLibraryStorage
    {
        public List<CaseDocumentTemplateStorage> Templates { get; set; } = new();
    }

    private sealed class CaseDocumentTemplateStorage
    {
        public string Key { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string DocumentType { get; set; } = string.Empty;
        public string Heading { get; set; } = string.Empty;
        public List<string> CaseTypes { get; set; } = new();
        public string? Description { get; set; }
        public string? Content { get; set; }
        public int Version { get; set; } = 1;
        public bool IsActive { get; set; } = true;
        public DateTime? UpdatedAt { get; set; }
    }

    private readonly record struct CaseDocumentTemplate(
        string DocumentKey,
        string DocumentType,
        string Status,
        string FileName,
        string Html);

    private readonly record struct ProceduralDeadlineValidationResult(
        string? Error,
        string DeadlineType = "",
        string Title = "",
        string Description = "",
        DateTime DueAt = default,
        bool AllDay = true,
        string Status = "active",
        int WarningDaysBefore = 7,
        string SourceType = "manual",
        string Notes = "");

    private readonly record struct CourtEventValidationResult(
        string? Error,
        string EventType = "",
        string Title = "",
        string Description = "",
        DateTime ScheduledAt = default,
        bool AllDay = false,
        string CourtInstance = "",
        string CourtName = "",
        string CourtCaseNumber = "",
        string Courtroom = "",
        string JudgeName = "",
        string SecretaryName = "",
        string MovementStatus = "",
        string ResultSummary = "",
        string SourceType = "manual");

    private readonly record struct CourtSyncApplyResult(
        IReadOnlyList<string> ChangedFields,
        int CreatedCourtEvents,
        int UpdatedCourtEvents);

    private readonly record struct CourtPartyValidationResult(
        string? Error,
        int? RelatedPartyId = null,
        string DisplayName = "",
        string Side = "other",
        string ProcessRole = "",
        string RepresentativeName = "",
        string Notes = "");

    private readonly record struct CourtDirectoryValidationResult(
        string? Error,
        string Name = "",
        string Instance = "first",
        string Code = "",
        string Url = "",
        string Address = "",
        string Contacts = "",
        bool IsActive = true,
        int SortOrder = 0);

    private readonly record struct CaseDocumentTemplateDefinition(
        string Key,
        string Title,
        string DocumentType,
        string Heading,
        IReadOnlyList<string>? CaseTypes = null,
        string? Description = null,
        string? Content = null,
        int Version = 1,
        bool IsActive = true,
        DateTime? UpdatedAt = null);

    private readonly record struct CaseDocumentTemplateTokenDefinition(
        string Key,
        string Category,
        string Description);
}
