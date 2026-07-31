using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Invoices;
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
    private const string CaseConversationAttachmentsMarker = "Вложения:";
    private const string CaseConversationDocumentType = "Переписка";
    private const string CaseConversationDocumentStatus = "Прикреплен к сообщению";
    private const string CaseHistoryCommentPrefix = "Комментарий по делу:";
    private const string CaseHistoryAuthorMarker = "Автор:";
    private const string CaseDocumentTemplatesSettingKey = "cases.documentTemplates";
    private const string CaseProductionCalendarSettingKey = "cases.productionCalendar";
    private const string CaseInactivityAutomationEnabledSettingKey = "tasks.automation.caseControl.enabled";
    private const string CaseInactivityAutomationDaysSettingKey = "tasks.automation.caseControl.inactiveDays";
    private const string CaseInactivityAutomationTag = "автоконтроль";
    private static readonly string[] ActiveTaskStatuses =
    [
        "new", "inProgress", "waiting", "onApproval", "approved", "revision", "overdueApproval"
    ];
    private static readonly JsonSerializerOptions CaseDocumentTemplateJsonOptions = new(JsonSerializerDefaults.Web);
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
    private static readonly string[] CourtInstanceOrder =
    [
        "first",
        "appeal",
        "cassation",
        "supreme",
    ];

    private readonly PaymentContext _db;
    private readonly ICaseDocumentStorage _caseDocumentStorage;
    private readonly IInvoiceDocumentPdfService? _pdfService;
    private readonly ISudrfCourtSyncService? _sudrfCourtSyncService;
    private readonly ICourtSyncProviderRegistry? _courtSyncProviderRegistry;
    private readonly IConfiguration _configuration;
    private readonly IOptionsMonitor<AppFeatureFlagsOptions>? _featureFlags;
    private readonly INotificationService? _notificationService;
    private readonly CourtSyncMetrics? _courtSyncMetrics;
    private readonly IExternalWorkOrderApprovalService? _externalWorkOrderApprovalService;
    private CaseStatusClock? _caseStatusClock;

    public CasesV2Controller(
        PaymentContext db,
        IWebHostEnvironment? environment = null,
        ICaseDocumentStorage? caseDocumentStorage = null,
        ISudrfCourtSyncService? sudrfCourtSyncService = null,
        IInvoiceDocumentPdfService? pdfService = null,
        ICourtSyncProviderRegistry? courtSyncProviderRegistry = null,
        IConfiguration? configuration = null,
        IOptionsMonitor<AppFeatureFlagsOptions>? featureFlags = null,
        INotificationService? notificationService = null,
        CourtSyncMetrics? courtSyncMetrics = null,
        IExternalWorkOrderApprovalService? externalWorkOrderApprovalService = null)
    {
        _db = db;
        _caseDocumentStorage = caseDocumentStorage ?? new LocalCaseDocumentStorage(environment, configuration);
        _pdfService = pdfService;
        _sudrfCourtSyncService = sudrfCourtSyncService;
        _courtSyncProviderRegistry = courtSyncProviderRegistry;
        _configuration = configuration ?? new ConfigurationBuilder().Build();
        _featureFlags = featureFlags;
        _notificationService = notificationService;
        _courtSyncMetrics = courtSyncMetrics;
        _externalWorkOrderApprovalService = externalWorkOrderApprovalService;
    }

    private Task<DataVisibilityAccess?> ResolveCasesVisibilityAsync(CancellationToken ct)
        => _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Cases, ct);

    private ActionResult MissingCasesVisibilityResult()
        => User.GetUserId().HasValue ? Forbid() : Unauthorized();

    private async Task<ActionResult?> RequireCasesPermissionAsync(
        Func<RolePermission, bool> predicate,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Forbid();
        }

        var isAdmin = await _db.IsUserAdminAsync(userId.Value, ct);
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

        var isAdmin = await _db.IsUserAdminAsync(userId.Value, ct);
        if (isAdmin)
        {
            return null;
        }

        var permission = await _db.GetUserSectionPermissionAsync(userId.Value, RolePermissionCatalog.Dictionaries, ct);
        return permission is not null && permission.CanEdit ? null : Forbid();
    }

    private async Task<ActionResult?> RequireSectionPermissionAsync(
        string section,
        Func<RolePermissionValues, bool> predicate,
        CancellationToken ct)
    {
        return await _db.HasUserSectionPermissionAsync(User, section, predicate, ct)
            ? null
            : Forbid();
    }

    private Task<bool> CanViewCaseFinanceAsync(CancellationToken ct)
        => _db.HasUserSectionPermissionAsync(User, RolePermissionCatalog.Payments, static permission => permission.CanView, ct);

    private async Task<bool> CanRevealCaseSensitiveDataAsync(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return false;
        }

        if (await _db.IsUserAdminAsync(userId.Value, ct))
        {
            return true;
        }

        var permission = await _db.GetUserSectionPermissionAsync(userId.Value, RolePermissionCatalog.Cases, ct);
        return permission?.CanEdit == true;
    }

    private static void HideCaseSensitiveData(ClientCase clientCase)
    {
        if (clientCase.Client is not null)
        {
            clientCase.Client.Phone = string.Empty;
            clientCase.Client.Email = string.Empty;
            clientCase.Client.Address = string.Empty;
            clientCase.Client.Notes = string.Empty;

            if (clientCase.Client.LegalEntity is not null)
            {
                HideLegalEntitySensitiveData(clientCase.Client.LegalEntity);
            }
        }

        if (clientCase.LegalEntity is not null)
        {
            HideLegalEntitySensitiveData(clientCase.LegalEntity);
        }
    }

    private static void HideLegalEntitySensitiveData(LegalEntity legalEntity)
    {
        legalEntity.Inn = null;
        legalEntity.Kpp = null;
        legalEntity.Ogrn = null;
        legalEntity.Address = null;
        legalEntity.Phone = null;
        legalEntity.Email = null;
        legalEntity.Director = null;
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
            return MissingCasesVisibilityResult();
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxRegistryPageSize);

        var canViewFinance = await CanViewCaseFinanceAsync(ct);
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
            canViewFinance ? minFee : null,
            canViewFinance ? maxFee : null,
            canViewFinance || !string.Equals(sortBy, "feeAmount", StringComparison.OrdinalIgnoreCase) ? sortBy : null,
            sortDir,
            scope,
            activity,
            inactiveDays);

        var total = await q.CountAsync(ct);
        var items = await SelectRegistryCaseRows(q
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .AsNoTracking())
            .ToListAsync(ct);
        if (canViewFinance)
        {
            await ApplyFinanceSummaryAsync(items, ct);
        }
        else
        {
            HideCaseFinanceData(items);
        }
        await ApplyTaskSummaryAsync(items, ct, includeInactivityControlSummary: true);
        PopulateResponsibleViewAliases(items);

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
        var (items, canViewFinance, error) = await LoadRegistryExportItemsAsync(
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
            inactiveDays,
            ct);
        if (error is not null)
        {
            return error;
        }

        var statusClock = await ResolveCaseStatusClockAsync(ct);
        var bytes = BuildRegistryCsv(items!, statusClock.TimeZone, canViewFinance);
        var fileName = $"cases-registry-{FormatUserExportTimestamp(statusClock.NowLocal)}.csv";
        return File(bytes, "text/csv; charset=utf-8", fileName);
    }

    [HttpGet("export.xls")]
    public async Task<IActionResult> ExportRegistryExcel(
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
        var (items, canViewFinance, error) = await LoadRegistryExportItemsAsync(
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
            inactiveDays,
            ct);
        if (error is not null)
        {
            return error;
        }

        var statusClock = await ResolveCaseStatusClockAsync(ct);
        var bytes = BuildRegistryExcel(items!, statusClock.NowLocal, statusClock.TimeZone, canViewFinance);
        var fileName = $"cases-registry-{FormatUserExportTimestamp(statusClock.NowLocal)}.xls";
        return File(bytes, "application/vnd.ms-excel; charset=utf-8", fileName);
    }

    [HttpGet("export.pdf")]
    public async Task<IActionResult> ExportRegistryPdf(
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
        var (items, canViewFinance, error) = await LoadRegistryExportItemsAsync(
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
            inactiveDays,
            ct);
        if (error is not null)
        {
            return error;
        }

        if (_pdfService is null)
        {
            return Problem("Сервис формирования PDF недоступен.");
        }

        var statusClock = await ResolveCaseStatusClockAsync(ct);
        var bytes = await _pdfService.RenderAsync(BuildRegistryPrintHtml(items!, statusClock.NowLocal, statusClock.TimeZone, canViewFinance), ct);
        var fileName = $"cases-registry-{FormatUserExportTimestamp(statusClock.NowLocal)}.pdf";
        return File(bytes, "application/pdf", fileName);
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
        var (items, canViewFinance, error) = await LoadRegistryExportItemsAsync(
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
            inactiveDays,
            ct);
        if (error is not null)
        {
            return error;
        }

        var statusClock = await ResolveCaseStatusClockAsync(ct);
        return Content(BuildRegistryPrintHtml(items!, statusClock.NowLocal, statusClock.TimeZone, canViewFinance), "text/html; charset=utf-8", Encoding.UTF8);
    }

    private async Task<(IReadOnlyList<ClientCase>? Items, bool CanViewFinance, IActionResult? Error)> LoadRegistryExportItemsAsync(
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
        int? inactiveDays,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanExport, ct) is { } forbidden)
        {
            return (null, false, forbidden);
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return (null, false, Unauthorized());
        }

        var canViewFinance = await CanViewCaseFinanceAsync(ct);
        var registryQuery = BuildRegistryQuery(
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
                canViewFinance ? minFee : null,
                canViewFinance ? maxFee : null,
                canViewFinance || !string.Equals(sortBy, "feeAmount", StringComparison.OrdinalIgnoreCase) ? sortBy : null,
                sortDir,
                scope,
                activity,
                inactiveDays)
            .Take(MaxRegistryExportRows)
            .AsNoTracking();
        var items = await SelectRegistryCaseRows(registryQuery)
            .ToListAsync(ct);
        if (canViewFinance)
        {
            await ApplyFinanceSummaryAsync(items, ct);
        }
        else
        {
            HideCaseFinanceData(items);
        }
        await ApplyTaskSummaryAsync(items, ct, includeInactivityControlSummary: true);
        PopulateResponsibleViewAliases(items);

        return (items, canViewFinance, null);
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
            .ApplyClientCaseVisibility(visibility)
            .ApplyCaseRegistryScope(scope, visibility.UserId)
            .ApplyCaseFilters(clientId, legalEntityId, status, search, responsibleUserId, caseType, priority, from, to, minFee, maxFee)
            .ApplyCaseActivityFilter(activity, inactiveDays, _db.TaskJournalItems.IgnoreQueryFilters())
            .ApplyCaseSort(sortBy, sortDir);

    private static IQueryable<ClientCase> SelectRegistryCaseRows(IQueryable<ClientCase> query)
        => query.Select(item => new ClientCase
        {
            Id = item.Id,
            ClientId = item.ClientId,
            Client = item.Client == null
                ? null
                : new Client
                {
                    Id = item.Client.Id,
                    Name = item.Client.Name,
                    ResponsibleUserId = item.Client.ResponsibleUserId,
                },
            LegalEntityId = item.LegalEntityId,
            LegalEntity = item.LegalEntity == null
                ? null
                : new LegalEntity
                {
                    Id = item.LegalEntity.Id,
                    ShortName = item.LegalEntity.ShortName,
                    FullName = item.LegalEntity.FullName,
                    ResponsibleUserId = item.LegalEntity.ResponsibleUserId,
                },
            CreatedAt = item.CreatedAt,
            CaseNumber = item.CaseNumber,
            CaseType = item.CaseType,
            Stage = item.Stage,
            ResponsibleUserId = item.ResponsibleUserId,
            ResponsibleUser = item.ResponsibleUser == null
                ? null
                : new User
                {
                    Id = item.ResponsibleUser.Id,
                    FullName = item.ResponsibleUser.FullName,
                    Email = item.ResponsibleUser.Email,
                },
            StartDate = item.StartDate,
            DueDate = item.DueDate,
            FeeAmount = item.FeeAmount,
            Priority = item.Priority,
            CourtName = item.CourtName,
            CourtCaseNumber = item.CourtCaseNumber,
            CourtUrl = item.CourtUrl,
            NextHearingAt = item.NextHearingAt,
            Claimant = item.Claimant,
            Defendant = item.Defendant,
            ThirdParties = item.ThirdParties,
            Representatives = item.Representatives,
            JudgeName = item.JudgeName,
            Courtroom = item.Courtroom,
            CourtSecretaryContacts = item.CourtSecretaryContacts,
            ClaimFiledAt = item.ClaimFiledAt,
            CourtDecisionAt = item.CourtDecisionAt,
            AppealDeadlineAt = item.AppealDeadlineAt,
            CassationDeadlineAt = item.CassationDeadlineAt,
            MotionDeadlineAt = item.MotionDeadlineAt,
            ObjectionDeadlineAt = item.ObjectionDeadlineAt,
            DocumentsSummary = item.DocumentsSummary,
            EventHistory = item.EventHistory,
            Strategy = item.Strategy,
            LastViewedAt = item.LastViewedAt,
            LastViewedByUserId = item.LastViewedByUserId,
            LastViewedByUserName = item.LastViewedByUserName,
            ActivityConfirmedAt = item.ActivityConfirmedAt,
            ActivityConfirmedByUserId = item.ActivityConfirmedByUserId,
            ActivityConfirmedByUserName = item.ActivityConfirmedByUserName,
            InactivityRemindersEnabled = item.InactivityRemindersEnabled,
            InactivityRemindersDisabledReason = item.InactivityRemindersDisabledReason,
            IsUnderControl = item.IsUnderControl,
            ControlMarkedAt = item.ControlMarkedAt,
            ControlMarkedByUserId = item.ControlMarkedByUserId,
            ControlMarkedByUserName = item.ControlMarkedByUserName,
            IsHiddenFromExecutor = item.IsHiddenFromExecutor,
            HiddenFromExecutorAt = item.HiddenFromExecutorAt,
            HiddenFromExecutorByUserId = item.HiddenFromExecutorByUserId,
            HiddenFromExecutorByUserName = item.HiddenFromExecutorByUserName,
            AccessRequestedAt = item.AccessRequestedAt,
            AccessRequestedByUserId = item.AccessRequestedByUserId,
            AccessRequestedByUserName = item.AccessRequestedByUserName,
            AccessRequestReason = item.AccessRequestReason,
            Description = item.Description,
            Status = item.Status,
            Title = item.Title,
        });

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
        var notificationService = _notificationService ?? CreateFallbackNotificationService();
        await notificationService.NotifyAsync(
            new NotificationEventEnvelope(
                recipientUserId,
                type,
                eventKey,
                title,
                body,
                "case",
                clientCase.Id,
                null,
                tone,
                metadata),
            ct);
    }

    private NotificationService CreateFallbackNotificationService()
        => new(
            _db,
            _configuration,
            new NoopEmailService(),
            new NoopTelegramNotificationService(),
            NullLogger<NotificationService>.Instance);

    private sealed class NoopEmailService : IEmailService
    {
        public Task SendRegistrationPendingEmailAsync(string toEmail, string fullName) => Task.CompletedTask;
        public Task SendRegistrationApprovedEmailAsync(string toEmail, string fullName) => Task.CompletedTask;
        public Task SendRegistrationRejectedEmailAsync(string toEmail, string fullName, string? reason) => Task.CompletedTask;
        public Task SendAdminNewRegistrationNotificationAsync(string adminEmail, string newUserFullName, string newUserEmail) => Task.CompletedTask;

        public Task SendEmailAsync(
            string toEmail,
            string subject,
            string body,
            bool isHtml = false,
            CancellationToken cancellationToken = default,
            EmailSendSettings? settings = null)
            => Task.CompletedTask;

        public Task SendEmailWithAttachmentAsync(string toEmail, string subject, string body, string attachmentFileName, byte[] attachmentBytes, string attachmentContentType)
            => Task.CompletedTask;
    }

    private sealed class NoopTelegramNotificationService : ITelegramNotificationService
    {
        public Task<TelegramSendResult> SendMessageAsync(long chatId, string text, CancellationToken cancellationToken = default)
            => Task.FromResult(new TelegramSendResult(true, NotificationDeliveryStatus.Sent));

        public Task<TelegramSendResult> SendMessageWithTokenAsync(
            long chatId,
            string text,
            string botToken,
            CancellationToken cancellationToken = default,
            TelegramBotConnectionSettings? connection = null)
            => Task.FromResult(new TelegramSendResult(true, NotificationDeliveryStatus.Sent));

        public Task<TelegramNotificationSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new TelegramNotificationSettingsDto());
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

    private async Task<IReadOnlyCollection<int>> ResolveCaseNotificationRecipientIdsAsync(int clientCaseId, CancellationToken ct)
    {
        var candidateUserIds = await _db.ClientCases
            .AsNoTracking()
            .Where(clientCase => clientCase.Id == clientCaseId)
            .Select(clientCase => new
            {
                clientCase.ResponsibleUserId,
                ClientResponsibleUserId = clientCase.Client != null ? clientCase.Client.ResponsibleUserId : null,
                LegalEntityResponsibleUserId = clientCase.LegalEntity != null ? clientCase.LegalEntity.ResponsibleUserId : null,
            })
            .ToListAsync(ct);

        var userIds = candidateUserIds
            .SelectMany(item => new[] { item.ResponsibleUserId, item.ClientResponsibleUserId, item.LegalEntityResponsibleUserId })
            .Where(userId => userId.HasValue && userId.Value > 0)
            .Select(userId => userId!.Value)
            .Distinct()
            .ToArray();
        if (userIds.Length == 0)
        {
            return Array.Empty<int>();
        }

        var allowedUserIds = await _db.Users
            .AsNoTracking()
            .Where(user => userIds.Contains(user.Id)
                && user.IsActive
                && user.IsApproved
                && user.Role != null
                && (user.Role.Name == "admin"
                    || user.Role.Permissions.Any(permission =>
                        permission.Section == RolePermissionCatalog.Cases
                        && permission.CanView)))
            .Select(user => user.Id)
            .ToListAsync(ct);

        var result = new List<int>();
        foreach (var userId in allowedUserIds)
        {
            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId.ToString(CultureInfo.InvariantCulture))],
                authenticationType: "CaseNotificationAccessCheck"));
            var visibility = await _db.ResolveDataVisibilityAsync(principal, RolePermissionCatalog.Cases, ct);
            if (visibility is null)
            {
                continue;
            }

            var canOpenCase = await _db.ClientCases
                .AsNoTracking()
                .Where(clientCase => clientCase.Id == clientCaseId)
                .ApplyClientCaseVisibility(visibility)
                .AnyAsync(ct);
            if (canOpenCase)
            {
                result.Add(userId);
            }
        }

        return result;
    }

    private async Task AddCourtEventUserNotificationsAsync(
        ClientCase clientCase,
        ClientCaseCourtEvent courtEvent,
        string action,
        int? actorUserId,
        string actorName,
        CancellationToken ct)
    {
        if (ClientCaseStageClassifier.IsPretrial(clientCase))
        {
            return;
        }

        var recipientUserIds = await ResolveCaseNotificationRecipientIdsAsync(clientCase.Id, ct);
        if (recipientUserIds.Count == 0)
        {
            return;
        }

        if (_notificationService is not null)
        {
            await _notificationService.NotifyCaseCourtEventAsync(
                clientCase,
                courtEvent,
                action,
                actorName,
                actorUserId ?? 0,
                recipientUserIds,
                ct);
            return;
        }

        var isCreated = string.Equals(action, "created", StringComparison.OrdinalIgnoreCase);
        var title = isCreated
            ? $"Новое судебное событие: {BuildCaseNotificationTitle(clientCase)}"
            : $"Обновлено судебное событие: {BuildCaseNotificationTitle(clientCase)}";
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
            var recipientTimeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, recipientUserId, ct);
            var body = BuildCourtEventNotificationBody(clientCase, courtEvent, isCreated, actorName, recipientTimeZone);
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

    private async Task NotifyCourtSyncFailedForCaseAsync(
        ClientCase clientCase,
        string providerName,
        string summary,
        int? actorUserId,
        string actorName,
        CancellationToken ct)
    {
        if (_notificationService is null || ClientCaseStageClassifier.IsPretrial(clientCase))
        {
            return;
        }

        var recipients = await ResolveCaseNotificationRecipientIdsAsync(clientCase.Id, ct);
        if (recipients.Count == 0)
        {
            return;
        }

        await _notificationService.NotifyCourtSyncFailedAsync(
            clientCase,
            providerName,
            summary,
            actorName,
            actorUserId ?? 0,
            recipients,
            ct);
    }

    private static string BuildCourtEventNotificationBody(
        ClientCase clientCase,
        ClientCaseCourtEvent courtEvent,
        bool isCreated,
        string actorName,
        TimeZoneInfo recipientTimeZone)
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
            .Append(FormatCaseNotificationDate(courtEvent.ScheduledAt, courtEvent.AllDay, recipientTimeZone))
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
        if (ClientCaseStageClassifier.IsPretrial(clientCase))
        {
            return;
        }

        var recipientUserIds = await ResolveCaseNotificationRecipientIdsAsync(clientCase.Id, ct);
        if (recipientUserIds.Count == 0)
        {
            return;
        }

        if (_notificationService is not null)
        {
            await _notificationService.NotifyCaseProceduralDeadlineAsync(
                clientCase,
                deadline,
                action,
                actorName,
                actorUserId ?? 0,
                recipientUserIds,
                ct);
            return;
        }

        var title = action switch
        {
            "created" => $"Новый процессуальный срок: {BuildCaseNotificationTitle(clientCase)}",
            "closed" => $"Закрыт процессуальный срок: {BuildCaseNotificationTitle(clientCase)}",
            _ => $"Обновлен процессуальный срок: {BuildCaseNotificationTitle(clientCase)}",
        };
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
            var recipientTimeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, recipientUserId, ct);
            var body = BuildProceduralDeadlineNotificationBody(clientCase, deadline, action, actorName, recipientTimeZone);
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
        string actorName,
        TimeZoneInfo recipientTimeZone)
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
            .Append(FormatCaseNotificationDate(deadline.DueAt, deadline.AllDay, recipientTimeZone))
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

    private static string FormatCaseNotificationDate(DateTime value, bool allDay, TimeZoneInfo recipientTimeZone)
    {
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        if (allDay)
        {
            return DateTime.SpecifyKind(value.Date, DateTimeKind.Unspecified).ToString("dd.MM.yyyy", culture);
        }

        return CalendarTimeZoneHelper
            .ToUserLocal(CalendarTimeZoneHelper.NormalizeUtcInstant(value), recipientTimeZone)
            .ToString("dd.MM.yyyy HH:mm", culture);
    }

    private async Task AddCaseDocumentUserNotificationsAsync(
        ClientCase clientCase,
        ClientCaseDocument document,
        string action,
        int? actorUserId,
        string actorName,
        CancellationToken ct)
    {
        var recipientUserIds = await ResolveCaseNotificationRecipientIdsAsync(clientCase.Id, ct);
        if (recipientUserIds.Count == 0)
        {
            return;
        }

        if (_notificationService is not null)
        {
            await _notificationService.NotifyCaseDocumentAsync(
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

    private async Task AddCaseConversationUserNotificationsAsync(
        ClientCase clientCase,
        string message,
        int attachmentCount,
        int? actorUserId,
        string actorName,
        DateTime occurredAtUtc,
        CancellationToken ct)
    {
        var recipientUserIds = await ResolveCaseNotificationRecipientIdsAsync(clientCase.Id, ct);
        if (recipientUserIds.Count == 0)
        {
            return;
        }

        var notificationService = _notificationService ?? CreateFallbackNotificationService();
        await notificationService.NotifyCaseConversationAsync(
            clientCase,
            message,
            attachmentCount,
            actorName,
            actorUserId ?? 0,
            recipientUserIds,
            occurredAtUtc,
            ct);
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

    private static byte[] BuildRegistryCsv(IReadOnlyCollection<ClientCase> items, TimeZoneInfo timeZone, bool canViewFinance)
    {
        var header = new List<string>
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
            "Следующий автоконтроль",
            "Причина исключения",
            "Просмотр ответственным",
            "Активность подтвердил",
            "Приоритет",
            "Суд",
            "№ в суде",
            "Заседание",
        };
        if (canViewFinance)
        {
            header.InsertRange(15, ["Гонорар", "Счета", "Оплачено", "Долг"]);
        }

        var lines = new List<string>
        {
            string.Join(';', header.Select(CsvCell)),
        };

        foreach (var item in items)
        {
            var values = new List<string?>
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
                FormatRuDateTime(GetLatestCaseActivityAt(item), timeZone),
                FormatRuDateTime(item.NextInactivityControlAt, timeZone),
                item.InactivityControlExclusionReason,
                FormatRuDateTime(item.LastViewedByResponsibleAt ?? item.LastViewedAt, timeZone),
                item.ActivityConfirmedByUserName,
                GetPriorityLabel(item.Priority),
                item.CourtName,
                item.CourtCaseNumber,
                FormatRuDateTime(item.NextHearingAt, timeZone),
            };
            if (canViewFinance)
            {
                values.InsertRange(15, [
                    FormatMoney(item.FeeAmount),
                    FormatMoney(item.IncomeTotal),
                    FormatMoney(item.IncomePaid),
                    FormatMoney(item.IncomeOutstanding),
                ]);
            }

            lines.Add(string.Join(';', values.Select(CsvCell)));
        }

        var csv = string.Join('\n', lines);
        var preamble = Encoding.UTF8.GetPreamble();
        var body = Encoding.UTF8.GetBytes(csv);
        var result = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, result, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, result, preamble.Length, body.Length);
        return result;
    }

    private static byte[] BuildRegistryExcel(IReadOnlyCollection<ClientCase> items, DateTime generatedAtLocal, TimeZoneInfo timeZone, bool canViewFinance)
    {
        var generatedAt = FormatUserGeneratedAt(generatedAtLocal);
        var totalFee = items.Sum(item => item.FeeAmount);
        var totalIncome = items.Sum(item => item.IncomeTotal);
        var totalPaid = items.Sum(item => item.IncomePaid);
        var totalDebt = items.Sum(item => item.IncomeOutstanding);
        var builder = new StringBuilder()
            .AppendLine("""<?xml version="1.0" encoding="utf-8"?>""")
            .AppendLine("""<?mso-application progid="Excel.Sheet"?>""")
            .AppendLine("""<Workbook xmlns="urn:schemas-microsoft-com:office:spreadsheet" xmlns:ss="urn:schemas-microsoft-com:office:spreadsheet">""")
            .AppendLine("""  <Worksheet ss:Name="Реестр дел">""")
            .AppendLine("    <Table>");

        AppendExcelRow(builder, "Реестр дел");
        AppendExcelRow(builder, "Сформировано", generatedAt);
        AppendExcelRow(builder, "Ограничение выгрузки", $"Первые {MaxRegistryExportRows} строк текущей выборки");
        AppendExcelRow(builder);
        AppendExcelRow(builder, "Дел", items.Count.ToString(CultureInfo.InvariantCulture));
        if (canViewFinance)
        {
            AppendExcelRow(builder, "Гонорар", FormatMoney(totalFee));
            AppendExcelRow(builder, "Счета", FormatMoney(totalIncome));
            AppendExcelRow(builder, "Оплачено", FormatMoney(totalPaid));
            AppendExcelRow(builder, "Долг", FormatMoney(totalDebt));
        }
        AppendExcelRow(builder);
        var header = new List<string?>
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
            "Следующий автоконтроль",
            "Причина исключения",
            "Просмотр ответственным",
            "Активность подтвердил",
            "Приоритет",
            "Суд",
            "№ в суде",
            "Заседание",
        };
        if (canViewFinance)
        {
            header.InsertRange(15, ["Гонорар", "Счета", "Оплачено", "Долг"]);
        }
        AppendExcelRow(builder, header.ToArray());

        foreach (var item in items)
        {
            var row = new List<string?>
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
                FormatRuDateTime(GetLatestCaseActivityAt(item), timeZone),
                FormatRuDateTime(item.NextInactivityControlAt, timeZone),
                item.InactivityControlExclusionReason,
                FormatRuDateTime(item.LastViewedByResponsibleAt ?? item.LastViewedAt, timeZone),
                item.ActivityConfirmedByUserName,
                GetPriorityLabel(item.Priority),
                item.CourtName,
                item.CourtCaseNumber,
                FormatRuDateTime(item.NextHearingAt, timeZone),
            };
            if (canViewFinance)
            {
                row.InsertRange(15, [
                    FormatMoney(item.FeeAmount),
                    FormatMoney(item.IncomeTotal),
                    FormatMoney(item.IncomePaid),
                    FormatMoney(item.IncomeOutstanding),
                ]);
            }
            AppendExcelRow(builder, row.ToArray());
        }

        builder
            .AppendLine("    </Table>")
            .AppendLine("  </Worksheet>")
            .AppendLine("</Workbook>");

        var preamble = Encoding.UTF8.GetPreamble();
        var body = Encoding.UTF8.GetBytes(builder.ToString());
        var result = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, result, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, result, preamble.Length, body.Length);
        return result;
    }

    private static string BuildRegistryPrintHtml(IReadOnlyCollection<ClientCase> items, DateTime generatedAtLocal, TimeZoneInfo timeZone, bool canViewFinance)
    {
        var generatedAt = FormatUserGeneratedAt(generatedAtLocal);
        var totalFee = items.Sum(item => item.FeeAmount);
        var totalIncome = items.Sum(item => item.IncomeTotal);
        var totalPaid = items.Sum(item => item.IncomePaid);
        var totalDebt = items.Sum(item => item.IncomeOutstanding);
        var summaryColumns = canViewFinance ? 5 : 1;
        var financeSummary = canViewFinance
            ? $"""
                <div><span>Гонорар</span><strong>{Html(FormatMoney(totalFee))}</strong></div>
                <div><span>Счета</span><strong>{Html(FormatMoney(totalIncome))}</strong></div>
                <div><span>Оплачено</span><strong>{Html(FormatMoney(totalPaid))}</strong></div>
                <div><span>Долг</span><strong>{Html(FormatMoney(totalDebt))}</strong></div>
              """
            : string.Empty;
        var financeHeaders = canViewFinance
            ? """
                    <th>Гонорар</th>
                    <th>Счета</th>
                    <th>Оплачено</th>
                    <th>Долг</th>
              """
            : string.Empty;
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
              {(canViewFinance ? $"""
              <td class="num">{Html(FormatMoney(item.FeeAmount))}</td>
              <td class="num">{Html(FormatMoney(item.IncomeTotal))}</td>
              <td class="num">{Html(FormatMoney(item.IncomePaid))}</td>
              <td class="num">{Html(FormatMoney(item.IncomeOutstanding))}</td>
              """ : string.Empty)}
              <td>{item.OpenTaskCount} / {item.OverdueTaskCount}</td>
              <td>{Html(FormatRuDateTime(GetLatestCaseActivityAt(item), timeZone))}</td>
              <td>{Html(FormatRuDateTime(item.NextInactivityControlAt, timeZone))}</td>
              <td>{Html(item.InactivityControlExclusionReason)}</td>
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
                .summary { display: grid; grid-template-columns: repeat({{summaryColumns}}, minmax(120px, 1fr)); gap: 8px; margin-bottom: 18px; }
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
                  .summary { grid-template-columns: repeat({{summaryColumns}}, 1fr); }
                }
              </style>
            </head>
            <body>
              <h1>Реестр дел</h1>
              <div class="meta">Сформировано: {{Html(generatedAt)}}. В печатную форму попали первые {{MaxRegistryExportRows}} строк текущей выборки.</div>
              <section class="summary">
                <div><span>Дел</span><strong>{{items.Count}}</strong></div>
                {{financeSummary}}
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
                    {{financeHeaders}}
                    <th>Задачи</th>
                    <th>Последняя активность</th>
                    <th>Следующий автоконтроль</th>
                    <th>Причина исключения</th>
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

    private static string FormatUserGeneratedAt(DateTime localDateTime)
        => localDateTime.ToString("dd.MM.yyyy HH:mm", CultureInfo.GetCultureInfo("ru-RU"));

    private static string FormatUserExportTimestamp(DateTime localDateTime)
        => localDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

    private static void AppendExcelRow(StringBuilder builder, params string?[] values)
    {
        builder.Append("      <Row>");
        foreach (var value in values)
        {
            builder.Append("<Cell><Data ss:Type=\"String\">")
                .Append(Html(value))
                .Append("</Data></Cell>");
        }

        builder.AppendLine("</Row>");
    }

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

    private static string FormatRuDateTime(DateTime? value, TimeZoneInfo timeZone)
        => value.HasValue
            ? CalendarTimeZoneHelper.FormatUserDateTime(value.Value, timeZone, culture: CultureInfo.GetCultureInfo("ru-RU"))
            : string.Empty;

    private static string FormatMoney(decimal value)
        => value.ToString("N2", CultureInfo.GetCultureInfo("ru-RU")) + " ₽";

    private static DateTime? GetLatestCaseActivityAt(ClientCase clientCase)
    {
        if (clientCase.LastMeaningfulActivityAt.HasValue)
        {
            return clientCase.LastMeaningfulActivityAt;
        }

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
        var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct);

        return Ok(new RenderedCaseDocumentTemplateDto
        {
            TemplateKey = template.Key,
            TemplateLabel = template.Title,
            DocumentType = documentType,
            FileName = $"{template.Title} - дело {caseNumber}.html",
            Html = BuildCaseDocumentTemplateHtml(clientCase, template, documentType, timeZone),
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

        var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct);
        var range = ResolveCaseCalendarDateRange(from, to, timeZone);
        if (range.ToDate < range.FromDate)
        {
            return BadRequest(new { message = "Дата окончания периода не может быть раньше даты начала." });
        }

        var events = await QueryCaseCalendarEventsAsync(visibility, range, clientCaseId, responsibleUserId, includeTasks, ct);
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

        var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct);
        var range = ResolveCaseCalendarDateRange(from, to, timeZone);
        if (range.ToDate < range.FromDate)
        {
            return BadRequest(new { message = "Дата окончания периода не может быть раньше даты начала." });
        }

        var events = await QueryCaseCalendarEventsAsync(visibility, range, clientCaseId, responsibleUserId, includeTasks, ct);
        return Ok(BuildCaseCalendarSummary(events, range));
    }

    [HttpGet("/api/case-calendar/events.ics")]
    public async Task<IActionResult> ExportCaseCalendarIcs(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int? clientCaseId,
        [FromQuery] int? responsibleUserId,
        [FromQuery] bool includeTasks = true,
        [FromQuery] string? reminderMinutes = null,
        CancellationToken ct = default)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanView, ct) is { } viewForbidden)
        {
            return viewForbidden;
        }

        if (await RequireCasesPermissionAsync(static permission => permission.CanExport, ct) is { } exportForbidden)
        {
            return exportForbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct);
        var range = ResolveCaseCalendarDateRange(from, to, timeZone);
        if (range.ToDate < range.FromDate)
        {
            return BadRequest(new { message = "Дата окончания периода не может быть раньше даты начала." });
        }

        var reminders = IcsReminderOptions.Normalize(reminderMinutes);
        var events = await QueryCaseCalendarEventsAsync(visibility, range, clientCaseId, responsibleUserId, includeTasks, ct);
        _db.ObjectAuditEvents.Add(CalendarExportAudit.CreateExportEvent(
            User,
            "cases",
            $"Выгружен ICS-календарь дел: {events.Count} событий.",
            events.Count,
            reminders,
            new
            {
                from = range.FromDate,
                to = range.ToDate,
                clientCaseId,
                responsibleUserId,
                includeTasks,
            }));
        await _db.SaveChangesAsync(ct);

        return File(
            Encoding.UTF8.GetBytes(BuildCaseCalendarIcs(events, reminders, timeZone)),
            "text/calendar; charset=utf-8",
            $"case-calendar-{range.FromDate:yyyyMMdd}-{range.ToDate:yyyyMMdd}.ics");
    }

    [HttpPost("procedural-deadlines/calculate")]
    public async Task<ActionResult<ProceduralDeadlineCalculationDto>> CalculateProceduralDeadlinePreview(
        [FromBody] ProceduralDeadlineCalculationRequest? request,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var calculationRequest = request ?? new ProceduralDeadlineCalculationRequest();
        var calendarSettings = await GetProductionCalendarSettingsAsync(ct);
        calculationRequest.WorkingDates = MergeCalculationDates(calculationRequest.WorkingDates, calendarSettings.WorkingDates);
        calculationRequest.AdditionalNonWorkingDates = MergeCalculationDates(calculationRequest.AdditionalNonWorkingDates, calendarSettings.NonWorkingDates);
        var result = ProceduralDeadlineCalculator.Calculate(calculationRequest);
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
            StartAt = result.StartAt,
            RawDeadlineAt = result.RawDeadlineAt,
            SkippedNonWorkingDays = result.SkippedNonWorkingDays,
            RollForwardDays = result.RollForwardDays,
            Explanation = result.Explanation,
            CalendarOverrides = MapProductionCalendarSettings(calendarSettings),
        });
    }

    [HttpGet("procedural-deadlines/calendar-settings")]
    public async Task<ActionResult<ProceduralDeadlineCalendarSettingsDto>> GetProceduralDeadlineCalendarSettings(CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        return Ok(MapProductionCalendarSettings(await GetProductionCalendarSettingsAsync(ct)));
    }

    [HttpPut("procedural-deadlines/calendar-settings")]
    public async Task<ActionResult<ProceduralDeadlineCalendarSettingsDto>> UpdateProceduralDeadlineCalendarSettings(
        [FromBody] UpsertProceduralDeadlineCalendarSettingsRequest? request,
        CancellationToken ct)
    {
        if (await RequireDocumentTemplateEditPermissionAsync(ct) is { } forbidden)
        {
            return forbidden;
        }

        var validation = BuildProductionCalendarSettings(request);
        if (validation.Error is not null)
        {
            return BadRequest(new { message = validation.Error });
        }

        var storage = validation.Settings;
        var now = DateTime.UtcNow;
        storage.UpdatedAt = now;
        var value = JsonSerializer.Serialize(storage, CaseDocumentTemplateJsonOptions);
        await UpsertSystemSettingAsync(CaseProductionCalendarSettingKey, value, now, ct);
        await _db.SaveChangesAsync(ct);

        return Ok(MapProductionCalendarSettings(storage));
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

        var canViewFinance = await CanViewCaseFinanceAsync(ct);
        var entity = await _db.ClientCases
            .WithCaseIncludes(includePayments: canViewFinance)
            .ApplyClientCaseVisibility(visibility)
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (entity is not null)
        {
            if (canViewFinance)
            {
                await ApplyFinanceSummaryAsync([entity], ct);
            }
            else
            {
                HideCaseFinanceData([entity]);
            }
            await ApplyTaskSummaryAsync([entity], ct);
            PopulateResponsibleViewAliases([entity]);
            var canRevealSensitiveData = await CanRevealCaseSensitiveDataAsync(ct);
            entity.CanRevealSensitiveData = canRevealSensitiveData;
            if (!canRevealSensitiveData)
            {
                HideCaseSensitiveData(entity);
            }
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

        var statusClock = await ResolveCaseStatusClockAsync(ct);
        var deadlines = await _db.ClientCaseProceduralDeadlines
            .AsNoTracking()
            .Where(item => item.ClientCaseId == id)
            .OrderBy(item => item.Status == "closed")
            .ThenBy(item => item.DueAt)
            .ThenBy(item => item.Id)
            .Select(item => MapProceduralDeadline(item, statusClock))
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

        return Created($"/api/v2/cases/{id}/procedural-deadlines/{deadline.Id}", MapProceduralDeadline(deadline, await ResolveCaseStatusClockAsync(ct)));
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

        return Ok(MapProceduralDeadline(deadline, await ResolveCaseStatusClockAsync(ct)));
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
        return Ok(MapProceduralDeadline(deadline, await ResolveCaseStatusClockAsync(ct)));
    }

    [HttpGet("{id:int}/court-instances")]
    public async Task<ActionResult<IReadOnlyList<ClientCaseCourtInstanceDto>>> GetCourtInstances(int id, CancellationToken ct)
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
            .AsNoTracking()
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var savedInstances = await _db.ClientCaseCourtInstances
            .AsNoTracking()
            .Include(item => item.Complaints.Where(complaint => !complaint.IsArchived))
            .Where(item => item.ClientCaseId == id)
            .ToListAsync(ct);

        var items = CourtInstanceOrder
            .Select(instance =>
            {
                var saved = savedInstances.FirstOrDefault(item =>
                    string.Equals(item.Instance, instance, StringComparison.OrdinalIgnoreCase));
                return saved is not null
                    ? MapCourtInstance(saved)
                    : MapCourtInstance(BuildDefaultCourtInstance(clientCase, instance));
            })
            .ToList();

        return Ok(items);
    }

    [HttpPut("{id:int}/court-instances/{instance}")]
    public async Task<ActionResult<ClientCaseCourtInstanceDto>> UpsertCourtInstance(
        int id,
        string instance,
        [FromBody] UpsertClientCaseCourtInstanceRequest? request,
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

        var normalizedInstance = NormalizeCourtInstance(instance);
        var court = await ResolveCourtDirectoryItemForRequestAsync(request?.CourtId, ct);
        if (request?.CourtId is not null && court is null)
        {
            return BadRequest(new { message = "Судебная инстанция не найдена в справочнике." });
        }

        var validation = ValidateCourtInstanceRequest(request, clientCase, normalizedInstance, court);
        if (validation.Error is not null)
        {
            return BadRequest(new { message = validation.Error });
        }

        var actorUserId = User.GetUserId();
        var actorName = User.GetDisplayName();
        var now = DateTime.UtcNow;
        var courtInstance = await _db.ClientCaseCourtInstances
            .Include(item => item.Complaints.Where(complaint => !complaint.IsArchived))
            .FirstOrDefaultAsync(item => item.ClientCaseId == id && item.Instance == normalizedInstance, ct);
        if (courtInstance is null)
        {
            courtInstance = new ClientCaseCourtInstance
            {
                ClientCaseId = id,
                Instance = normalizedInstance,
                CreatedAt = now,
                CreatedByUserId = actorUserId,
                CreatedByUserName = Truncate(actorName, 256),
            };
            _db.ClientCaseCourtInstances.Add(courtInstance);
        }

        courtInstance.CourtId = court?.Id;
        courtInstance.CourtName = validation.CourtName;
        courtInstance.CourtCaseNumber = validation.CourtCaseNumber;
        courtInstance.CourtUrl = validation.CourtUrl;
        courtInstance.JudgeName = validation.JudgeName;
        courtInstance.Courtroom = validation.Courtroom;
        courtInstance.SecretaryName = validation.SecretaryName;
        courtInstance.Notes = validation.Notes;
        courtInstance.UpdatedAt = now;
        courtInstance.UpdatedByUserId = actorUserId;
        courtInstance.UpdatedByUserName = Truncate(actorName, 256);

        AppendCaseEvent(clientCase, $"Обновлена судебная инстанция «{ResolveCourtInstanceLabel(normalizedInstance)}». Автор: {actorName}.");
        _db.SetAuditReason($"Обновлена судебная инстанция дела: {ResolveCourtInstanceLabel(normalizedInstance)}");
        await _db.SaveChangesAsync(ct);

        return Ok(MapCourtInstance(courtInstance));
    }

    [HttpPost("{id:int}/court-instances/{instance}/complaints")]
    public async Task<ActionResult<ClientCaseCourtComplaintDto>> CreateCourtComplaint(
        int id,
        string instance,
        [FromBody] UpsertClientCaseCourtComplaintRequest? request,
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

        var normalizedInstance = NormalizeCourtInstance(instance);
        var validation = await ValidateCourtComplaintRequestAsync(id, normalizedInstance, request, ct);
        if (validation.Error is not null)
        {
            return BadRequest(new { message = validation.Error });
        }

        var actorUserId = User.GetUserId();
        var actorName = User.GetDisplayName();
        var now = DateTime.UtcNow;
        var courtInstance = await EnsureCourtInstanceAsync(clientCase, normalizedInstance, actorUserId, actorName, ct);
        var complaint = new ClientCaseCourtComplaint
        {
            ClientCaseId = id,
            CourtInstanceId = courtInstance.Id,
            ComplaintType = validation.ComplaintType,
            Title = validation.Title,
            TargetInstance = validation.TargetInstance,
            FiledAt = validation.FiledAt,
            Status = validation.Status,
            ClientCaseDocumentId = validation.ClientCaseDocumentId,
            Notes = validation.Notes,
            CreatedAt = now,
            CreatedByUserId = actorUserId,
            CreatedByUserName = Truncate(actorName, 256),
            UpdatedAt = now,
            UpdatedByUserId = actorUserId,
            UpdatedByUserName = Truncate(actorName, 256),
        };

        _db.ClientCaseCourtComplaints.Add(complaint);
        AppendCaseEvent(clientCase, $"Добавлена жалоба «{complaint.Title}» в блок {ResolveCourtInstanceLabel(normalizedInstance)}. Автор: {actorName}.");
        _db.SetAuditReason($"Добавлена жалоба по судебной инстанции: {complaint.Title}");
        await _db.SaveChangesAsync(ct);

        return Created($"/api/v2/cases/{id}/court-instances/{normalizedInstance}/complaints/{complaint.Id}", MapCourtComplaint(complaint));
    }

    [HttpPut("{id:int}/court-instances/{instance}/complaints/{complaintId:int}")]
    public async Task<ActionResult<ClientCaseCourtComplaintDto>> UpdateCourtComplaint(
        int id,
        string instance,
        int complaintId,
        [FromBody] UpsertClientCaseCourtComplaintRequest? request,
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

        var normalizedInstance = NormalizeCourtInstance(instance);
        var complaint = await _db.ClientCaseCourtComplaints
            .Include(item => item.CourtInstance)
            .FirstOrDefaultAsync(item =>
                item.Id == complaintId &&
                item.ClientCaseId == id &&
                item.CourtInstance.Instance == normalizedInstance &&
                !item.IsArchived,
                ct);
        if (complaint is null)
        {
            return NotFound();
        }

        var validation = await ValidateCourtComplaintRequestAsync(id, normalizedInstance, request, ct);
        if (validation.Error is not null)
        {
            return BadRequest(new { message = validation.Error });
        }

        var actorUserId = User.GetUserId();
        var actorName = User.GetDisplayName();
        complaint.ComplaintType = validation.ComplaintType;
        complaint.Title = validation.Title;
        complaint.TargetInstance = validation.TargetInstance;
        complaint.FiledAt = validation.FiledAt;
        complaint.Status = validation.Status;
        complaint.ClientCaseDocumentId = validation.ClientCaseDocumentId;
        complaint.Notes = validation.Notes;
        complaint.UpdatedAt = DateTime.UtcNow;
        complaint.UpdatedByUserId = actorUserId;
        complaint.UpdatedByUserName = Truncate(actorName, 256);

        AppendCaseEvent(clientCase, $"Обновлена жалоба «{complaint.Title}». Автор: {actorName}.");
        _db.SetAuditReason($"Обновлена жалоба по судебной инстанции: {complaint.Title}");
        await _db.SaveChangesAsync(ct);

        return Ok(MapCourtComplaint(complaint));
    }

    [HttpDelete("{id:int}/court-instances/{instance}/complaints/{complaintId:int}")]
    public async Task<IActionResult> ArchiveCourtComplaint(
        int id,
        string instance,
        int complaintId,
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

        var normalizedInstance = NormalizeCourtInstance(instance);
        var complaint = await _db.ClientCaseCourtComplaints
            .Include(item => item.CourtInstance)
            .FirstOrDefaultAsync(item =>
                item.Id == complaintId &&
                item.ClientCaseId == id &&
                item.CourtInstance.Instance == normalizedInstance &&
                !item.IsArchived,
                ct);
        if (complaint is null)
        {
            return NotFound();
        }

        var actorUserId = User.GetUserId();
        var actorName = User.GetDisplayName();
        complaint.IsArchived = true;
        complaint.UpdatedAt = DateTime.UtcNow;
        complaint.UpdatedByUserId = actorUserId;
        complaint.UpdatedByUserName = Truncate(actorName, 256);
        AppendCaseEvent(clientCase, $"Жалоба «{complaint.Title}» перенесена в архив. Автор: {actorName}.");
        _db.SetAuditReason($"Жалоба по судебной инстанции перенесена в архив: {complaint.Title}");
        await _db.SaveChangesAsync(ct);

        return NoContent();
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

        var statusClock = await ResolveCaseStatusClockAsync(ct);
        var items = await _db.ClientCaseCourtEvents
            .AsNoTracking()
            .Where(item => item.ClientCaseId == id)
            .OrderBy(item => item.ScheduledAt)
            .ThenBy(item => item.Id)
            .Select(item => MapCourtEvent(item, statusClock))
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

        return Created($"/api/v2/cases/{id}/court-events/{courtEvent.Id}", MapCourtEvent(courtEvent, await ResolveCaseStatusClockAsync(ct)));
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

        return Ok(MapCourtEvent(courtEvent, await ResolveCaseStatusClockAsync(ct)));
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
        if (_notificationService is not null)
        {
            await _notificationService.NotifyCaseAccessRequestedAsync(
                clientCase,
                actorName,
                currentUserId.Value,
                reason,
                now,
                managerUserIds,
                ct);
        }
        else
        {
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
            .Where(item => item.AccessRequestedAt.HasValue)
            .OrderByDescending(item => item.AccessRequestedAt)
            .Take(100)
            .AsNoTracking()
            .Select(item => new ClientCaseAccessRequestRow
            {
                ClientCaseId = item.Id,
                CaseNumber = item.CaseNumber,
                Title = item.Title,
                ClientName = item.Client != null ? item.Client.Name : null,
                LegalEntityShortName = item.LegalEntity != null ? item.LegalEntity.ShortName : null,
                LegalEntityFullName = item.LegalEntity != null ? item.LegalEntity.FullName : null,
                ResponsibleUserId = item.ResponsibleUserId,
                ResponsibleFullName = item.ResponsibleUser != null ? item.ResponsibleUser.FullName : null,
                ResponsibleEmail = item.ResponsibleUser != null ? item.ResponsibleUser.Email : null,
                RequestedAt = item.AccessRequestedAt!.Value,
                AccessRequestedByUserId = item.AccessRequestedByUserId,
                AccessRequestedByUserName = item.AccessRequestedByUserName,
                Reason = item.AccessRequestReason,
                IsHiddenFromExecutor = item.IsHiddenFromExecutor,
            })
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
            if (_notificationService is not null)
            {
                await _notificationService.NotifyCaseAccessResolvedAsync(
                    clientCase,
                    grant,
                    actorName,
                    actorUserId ?? 0,
                    requestedByUserId.Value,
                    requestedByUserName,
                    requestReason,
                    comment,
                    resolvedAt,
                    ct);
            }
            else
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

        var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct);
        var timeline = SplitCaseHistory(clientCase.EventHistory)
            .Select((line, index) => MapTimelineEntry(clientCase.Id, line, index, timeZone))
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

        var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct);
        var conversation = MapCaseConversation(clientCase.Id, clientCase.EventHistory, timeZone);
        await PopulateConversationAttachmentsAsync(conversation, ct);
        return Ok(conversation);
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

        var canViewFinance = await CanViewCaseFinanceAsync(ct);
        var clientCase = await _db.ClientCases
            .WithCaseIncludes(includePayments: canViewFinance)
            .ApplyClientCaseVisibility(visibility)
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        if (canViewFinance)
        {
            await ApplyFinanceSummaryAsync([clientCase], ct);
        }
        else
        {
            HideCaseFinanceData([clientCase]);
        }
        await ApplyTaskSummaryAsync([clientCase], ct);

        return Ok(MapCaseSummary(clientCase, canViewFinance));
    }

    [HttpGet("{id:int}/finance")]
    public async Task<ActionResult<ClientCaseFinanceDetailDto>> GetFinance(int id, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }
        if (!await CanViewCaseFinanceAsync(ct))
        {
            return Forbid();
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
        var statusClock = await ResolveCaseStatusClockAsync(ct);

        return Ok(new ClientCaseFinanceDetailDto
        {
            CaseId = clientCase.Id,
            CaseTitle = clientCase.Title,
            OwnerName = ResolveCaseOwnerName(clientCase),
            Totals = BuildFinanceTotals(clientCase, visiblePayments),
            Calendar = BuildFinanceCalendar(visiblePayments, statusClock.TodayDate),
            Invoices = BuildFinanceInvoices(visiblePayments, linkedContracts, statusClock.TodayDate),
            Payments = visiblePayments.Select(payment => MapFinancePayment(payment, statusClock.TodayDate)).ToList(),
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
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var storedDocuments = await _db.ClientCaseDocuments
            .AsNoTracking()
            .Where(item => item.ClientCaseId == id)
            .OrderBy(item => item.DocumentType)
            .ThenBy(item => item.OriginalFileName)
            .ThenByDescending(item => item.Version)
            .ThenByDescending(item => item.Id)
            .ToListAsync(ct);
        var documents = storedDocuments
            .Where(IsCaseDocumentAvailable)
            .ToList();

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

        var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct);
        var template = BuildCaseDocumentTemplate(clientCase, request, templateDefinitionResult.Template, timeZone);
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
            await AddCaseDocumentUserNotificationsAsync(
                clientCase,
                document,
                "generated",
                User.GetUserId(),
                User.GetDisplayName(),
                ct);
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

        AddCaseDocumentAuditEvent(clientCase, document, "Downloaded", $"Документ «{document.OriginalFileName}» скачан.");
        await _db.SaveChangesAsync(ct);

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

        var clientCase = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == document.ClientCaseId, ct);
        if (clientCase is null || !_caseDocumentStorage.Exists(document.StoragePath))
        {
            return NotFound();
        }

        AddCaseDocumentAuditEvent(clientCase, document, "Downloaded", $"Документ «{document.OriginalFileName}» скачан.");
        await _db.SaveChangesAsync(ct);

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
        var uploadError = UploadValidationHelper.ValidateRequiredFile(
            file,
            "Выберите файл для загрузки.",
            MaxCaseDocumentBytes,
            "Файл не должен превышать 25 МБ.");
        if (uploadError is not null)
        {
            return BadRequest(new { message = uploadError.Message });
        }
        var uploadFile = file!;

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
        var safeFileName = SanitizeFileName(uploadFile.FileName);
        var storedFileName = BuildStoredFileName(normalizedDocumentKey!, version, safeFileName);
        var relativePath = _caseDocumentStorage.BuildRelativePath(clientCase.Id, storedFileName);

        try
        {
            await using var stream = uploadFile.OpenReadStream();
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
                ContentType = string.IsNullOrWhiteSpace(uploadFile.ContentType) ? "application/octet-stream" : uploadFile.ContentType,
                SizeBytes = uploadFile.Length,
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
            await AddCaseDocumentUserNotificationsAsync(
                clientCase,
                document,
                isNewDocument ? "uploaded" : "version-created",
                User.GetUserId(),
                User.GetDisplayName(),
                ct);
            await _db.SaveChangesAsync(ct);

            return Created($"/api/case-documents/{document.Id}", MapDocument(document, true));
        }
        catch
        {
            _caseDocumentStorage.DeleteIfExists(relativePath);
            throw;
        }
    }

    private async Task<ActionResult<IReadOnlyList<ClientCaseConversationEntryDto>>> AddConversationEntryCore(
        ClientCase clientCase,
        string message,
        IReadOnlyList<IFormFile> files,
        CancellationToken ct)
    {
        var attachmentFiles = files
            .Where(static file => file is not null)
            .ToList();
        foreach (var file in attachmentFiles)
        {
            var uploadError = UploadValidationHelper.ValidateRequiredFile(
                file,
                "Прикрепленный файл пустой.",
                MaxCaseDocumentBytes,
                "Файл вложения не должен превышать 25 МБ.");
            if (uploadError is not null)
            {
                return BadRequest(new { message = uploadError.Message });
            }
        }

        var storedPaths = new List<string>();
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var documents = new List<ClientCaseDocument>();
            foreach (var file in attachmentFiles)
            {
                var document = await SaveConversationAttachmentDocumentAsync(clientCase, file, ct);
                documents.Add(document);
                storedPaths.Add(document.StoragePath);
            }

            if (documents.Count > 0)
            {
                await _db.SaveChangesAsync(ct);
            }

            var attachmentsText = documents.Count == 0
                ? string.Empty
                : $" {CaseConversationAttachmentsMarker} {string.Join(", ", documents.Select(static document => document.Id))}.";
            var actorUserId = User.GetUserId();
            var actorName = User.GetDisplayName();
            var occurredAtUtc = DateTime.UtcNow;
            AppendCaseEvent(
                clientCase,
                $"{CaseConversationPrefix} {message}.{attachmentsText} {CaseHistoryAuthorMarker} {actorName}.");

            foreach (var document in documents)
            {
                AddCaseDocumentAuditEvent(
                    clientCase,
                    document,
                    "ConversationAttached",
                    $"Файл «{document.OriginalFileName}» прикреплен к сообщению переписки дела.");
            }

            await AddCaseConversationUserNotificationsAsync(
                clientCase,
                message,
                documents.Count,
                actorUserId,
                actorName,
                occurredAtUtc,
                ct);

            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            foreach (var path in storedPaths)
            {
                _caseDocumentStorage.DeleteIfExists(path);
            }

            throw;
        }

        var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct);
        var conversation = MapCaseConversation(clientCase.Id, clientCase.EventHistory, timeZone);
        await PopulateConversationAttachmentsAsync(conversation, ct);
        return Ok(conversation);
    }

    private async Task<ClientCaseDocument> SaveConversationAttachmentDocumentAsync(
        ClientCase clientCase,
        IFormFile file,
        CancellationToken ct)
    {
        var documentKey = Guid.NewGuid().ToString("N");
        var safeFileName = SanitizeFileName(file.FileName);
        var storedFileName = BuildStoredFileName(documentKey, 1, safeFileName);
        var relativePath = _caseDocumentStorage.BuildRelativePath(clientCase.Id, storedFileName);

        await using var stream = file.OpenReadStream();
        await _caseDocumentStorage.SaveAsync(relativePath, stream, ct);

        var document = new ClientCaseDocument
        {
            ClientCaseId = clientCase.Id,
            DocumentKey = documentKey,
            Version = 1,
            DocumentType = CaseConversationDocumentType,
            Status = CaseConversationDocumentStatus,
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
        return document;
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
            await AddCaseDocumentUserNotificationsAsync(
                clientCase,
                document,
                string.Equals(auditAction, "Created", StringComparison.OrdinalIgnoreCase) ? "uploaded" : "generated",
                User.GetUserId(),
                User.GetDisplayName(),
                ct);
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
            await AddCaseDocumentUserNotificationsAsync(
                clientCase,
                document,
                "updated",
                User.GetUserId(),
                User.GetDisplayName(),
                ct);
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
        await AddCaseDocumentUserNotificationsAsync(
            clientCase,
            document,
            "deleted",
            User.GetUserId(),
            User.GetDisplayName(),
            ct);
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

    private readonly record struct CaseCalendarDateRange(
        DateTime FromDate,
        DateTime ToDate,
        DateTime ToDateExclusive,
        DateTime FromInstantUtc,
        DateTime ToInstantExclusiveUtc,
        DateTime TodayDate,
        DateTime NowInstantUtc);

    private readonly record struct CaseStatusClock(DateTime TodayDate, DateTime NowInstantUtc, DateTime NowLocal, TimeZoneInfo TimeZone);

    private async Task<CaseStatusClock> ResolveCaseStatusClockAsync(CancellationToken ct)
    {
        if (_caseStatusClock is { } cachedClock)
        {
            return cachedClock;
        }

        var nowUtc = DateTime.UtcNow;
        var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct);
        var nowLocal = CalendarTimeZoneHelper.ToUserLocal(nowUtc, timeZone);
        var clock = new CaseStatusClock(nowLocal.Date, nowUtc, nowLocal, timeZone);
        _caseStatusClock = clock;
        return clock;
    }

    private CaseStatusClock ResolveCaseStatusClock()
    {
        if (_caseStatusClock is { } cachedClock)
        {
            return cachedClock;
        }

        var nowUtc = DateTime.UtcNow;
        var userId = User.GetUserId();
        var timeZoneId = userId.HasValue
            ? _db.UserSettings
                .AsNoTracking()
                .Where(item => item.UserId == userId.Value && item.Key == CalendarTimeZoneHelper.UserTimeZoneSettingKey)
                .Select(item => item.Value)
                .FirstOrDefault()
            : null;
        var timeZone = CalendarTimeZoneHelper.ResolveTimeZone(timeZoneId);
        var nowLocal = CalendarTimeZoneHelper.ToUserLocal(nowUtc, timeZone);
        var clock = new CaseStatusClock(nowLocal.Date, nowUtc, nowLocal, timeZone);
        _caseStatusClock = clock;
        return clock;
    }

    private static CaseCalendarDateRange ResolveCaseCalendarDateRange(DateTime? from, DateTime? to, TimeZoneInfo timeZone)
    {
        var nowUtc = DateTime.UtcNow;
        var localToday = CalendarTimeZoneHelper.ToUserLocal(nowUtc, timeZone).Date;
        var fromDate = (from ?? localToday.AddDays(-30)).Date;
        var toDate = (to ?? fromDate.AddDays(90)).Date;
        var toDateExclusive = toDate.AddDays(1);

        return new CaseCalendarDateRange(
            fromDate,
            toDate,
            toDateExclusive,
            CalendarTimeZoneHelper.FromUserLocalToUtc(fromDate, timeZone),
            CalendarTimeZoneHelper.FromUserLocalToUtc(toDateExclusive, timeZone),
            localToday,
            nowUtc);
    }

    private async Task<IReadOnlyList<CaseCalendarEventDto>> QueryCaseCalendarEventsAsync(
        DataVisibilityAccess visibility,
        CaseCalendarDateRange range,
        int? clientCaseId,
        int? responsibleUserId,
        bool includeTasks,
        CancellationToken ct)
    {
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
            (item.DueDate.HasValue && item.DueDate.Value >= range.FromDate && item.DueDate.Value < range.ToDateExclusive) ||
            (item.NextHearingAt.HasValue && item.NextHearingAt.Value >= range.FromInstantUtc && item.NextHearingAt.Value < range.ToInstantExclusiveUtc) ||
            (item.ClaimFiledAt.HasValue && item.ClaimFiledAt.Value >= range.FromDate && item.ClaimFiledAt.Value < range.ToDateExclusive) ||
            (item.CourtDecisionAt.HasValue && item.CourtDecisionAt.Value >= range.FromDate && item.CourtDecisionAt.Value < range.ToDateExclusive) ||
            (item.AppealDeadlineAt.HasValue && item.AppealDeadlineAt.Value >= range.FromDate && item.AppealDeadlineAt.Value < range.ToDateExclusive) ||
            (item.CassationDeadlineAt.HasValue && item.CassationDeadlineAt.Value >= range.FromDate && item.CassationDeadlineAt.Value < range.ToDateExclusive) ||
            (item.MotionDeadlineAt.HasValue && item.MotionDeadlineAt.Value >= range.FromDate && item.MotionDeadlineAt.Value < range.ToDateExclusive) ||
            (item.ObjectionDeadlineAt.HasValue && item.ObjectionDeadlineAt.Value >= range.FromDate && item.ObjectionDeadlineAt.Value < range.ToDateExclusive) ||
            _db.ClientCaseCourtEvents.Any(courtEvent =>
                courtEvent.ClientCaseId == item.Id &&
                courtEvent.MovementStatus != "cancelled" &&
                courtEvent.MovementStatus != "archived" &&
                (courtEvent.AllDay
                    ? courtEvent.ScheduledAt >= range.FromDate && courtEvent.ScheduledAt < range.ToDateExclusive
                    : courtEvent.ScheduledAt >= range.FromInstantUtc && courtEvent.ScheduledAt < range.ToInstantExclusiveUtc)) ||
            _db.ClientCaseProceduralDeadlines.Any(deadline =>
                deadline.ClientCaseId == item.Id &&
                deadline.Status != "closed" &&
                deadline.Status != "cancelled" &&
                deadline.Status != "archived" &&
                (deadline.AllDay
                    ? deadline.DueAt >= range.FromDate && deadline.DueAt < range.ToDateExclusive
                    : deadline.DueAt >= range.FromInstantUtc && deadline.DueAt < range.ToInstantExclusiveUtc)) ||
            (includeTasks && _db.TaskJournalItems.Any(task =>
                task.ClientCaseId == item.Id &&
                task.DueAt >= range.FromInstantUtc &&
                task.DueAt < range.ToInstantExclusiveUtc &&
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
                .Select(item => item.DeadlineType)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (clientCase.CourtEvents.Any(item => item.EventType == "hearing"))
            {
                separateDeadlineTypes.Add("hearing");
            }

            AddCaseCalendarDateEvents(events, clientCase, separateDeadlineTypes, range);
            AddCaseCalendarCourtEvents(events, clientCase, range);
            AddCaseCalendarProceduralDeadlineEvents(events, clientCase, range);
        }

        if (includeTasks && cases.Count > 0)
        {
            var visibleCaseIds = cases.Select(item => item.Id).ToArray();
            var tasks = await _db.TaskJournalItems
                .AsNoTracking()
                .Where(item => item.ClientCaseId.HasValue && visibleCaseIds.Contains(item.ClientCaseId.Value))
                .Where(item => item.DueAt >= range.FromInstantUtc && item.DueAt < range.ToInstantExclusiveUtc)
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
                    events.Add(MapTaskCalendarEvent(clientCase, task, range));
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
        CaseCalendarDateRange range)
    {
        return new CaseCalendarSummaryDto
        {
            From = range.FromDate,
            To = range.ToDate,
            TotalEventCount = events.Count,
            OverdueCount = events.Count(static item => item.IsOverdue),
            WarningCount = events.Count(static item => item.IsWarning),
            CourtEventCount = events.Count(static item => item.Kind == "court"),
            ProceduralDeadlineCount = events.Count(static item => item.Kind == "procedural-deadline"),
            CaseDeadlineCount = events.Count(static item => item.Kind == "case-deadline"),
            TaskCount = events.Count(static item => item.Kind == "task"),
            NearestEvent = events
                .Where(item => item.AllDay
                    ? item.StartAt.Date >= range.TodayDate
                    : item.StartAt >= range.NowInstantUtc)
                .OrderBy(static item => item.StartAt)
                .FirstOrDefault()
                ?? events.OrderByDescending(static item => item.StartAt).FirstOrDefault(),
        };
    }

    private static string BuildCaseCalendarIcs(IEnumerable<CaseCalendarEventDto> events, IReadOnlyList<int>? reminderMinutes = null, TimeZoneInfo? timeZone = null)
    {
        var now = DateTime.UtcNow;
        var reminders = reminderMinutes is { Count: > 0 } ? reminderMinutes : IcsReminderOptions.DefaultReminderMinutes;
        var calendarTimeZone = timeZone ?? CalendarTimeZoneHelper.ResolveTimeZone(null);
        var builder = new StringBuilder();
        builder.AppendLine("BEGIN:VCALENDAR");
        builder.AppendLine("VERSION:2.0");
        builder.AppendLine("PRODID:-//PayPlanner//Case Calendar//RU");
        builder.AppendLine("CALSCALE:GREGORIAN");
        builder.AppendLine("METHOD:PUBLISH");
        builder.AppendLine("X-WR-CALNAME:Календарь дел");

        foreach (var item in events)
        {
            builder.AppendLine("BEGIN:VEVENT");
            builder.AppendLine($"UID:payplanner-case-calendar-{EscapeIcsText(item.Id)}@crm.elitpravo.com");
            builder.AppendLine($"DTSTAMP:{FormatIcsDateTime(now, calendarTimeZone)}");

            if (item.AllDay)
            {
                var start = DateTime.SpecifyKind(item.StartAt.Date, DateTimeKind.Utc);
                var end = DateTime.SpecifyKind(item.EndAt.Date, DateTimeKind.Utc);
                if (end <= start)
                {
                    end = start.AddDays(1);
                }

                builder.AppendLine($"DTSTART;VALUE=DATE:{start:yyyyMMdd}");
                builder.AppendLine($"DTEND;VALUE=DATE:{end:yyyyMMdd}");
            }
            else
            {
                var start = item.StartAt;
                var end = item.EndAt;
                if (end <= start)
                {
                    end = start.AddHours(1);
                }

                builder.AppendLine($"DTSTART:{FormatIcsDateTime(start, calendarTimeZone)}");
                builder.AppendLine($"DTEND:{FormatIcsDateTime(end, calendarTimeZone)}");
            }

            builder.AppendLine($"SUMMARY:{EscapeIcsText(item.Title)}");
            builder.AppendLine($"DESCRIPTION:{EscapeIcsText(BuildCaseCalendarIcsDescription(item))}");
            builder.AppendLine("STATUS:CONFIRMED");
            if (!item.AllDay)
            {
                foreach (var minutes in reminders)
                {
                    AppendCaseCalendarIcsAlarm(builder, item, IcsReminderOptions.ToTriggerDuration(minutes));
                }
            }

            builder.AppendLine("END:VEVENT");
        }

        builder.AppendLine("END:VCALENDAR");
        return builder.ToString().ReplaceLineEndings("\r\n");
    }

    private static void AppendCaseCalendarIcsAlarm(StringBuilder builder, CaseCalendarEventDto item, string triggerBeforeStart)
    {
        builder.AppendLine("BEGIN:VALARM");
        builder.AppendLine("ACTION:DISPLAY");
        builder.AppendLine($"DESCRIPTION:{EscapeIcsText($"Напоминание: {item.Title}")}");
        builder.AppendLine($"TRIGGER:-{triggerBeforeStart}");
        builder.AppendLine("END:VALARM");
    }

    private static string BuildCaseCalendarIcsDescription(CaseCalendarEventDto item)
        => string.Join("\\n", new[]
        {
            item.CaseTitle,
            item.ClientName,
            item.LegalEntityName,
            item.ResponsibleName,
            item.CourtName,
            item.CourtCaseNumber,
            item.Kind,
            item.DeadlineType,
        }.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static string FormatIcsDateTime(DateTime value, TimeZoneInfo timeZone)
        => CalendarTimeZoneHelper.FormatUtcIcsDateTime(value, timeZone);

    private static string EscapeIcsText(string? value)
        => (value ?? string.Empty)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(";", "\\;", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal)
            .Replace("\r\n", "\\n", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\n", StringComparison.Ordinal);

    private static void AddCaseCalendarDateEvents(
        ICollection<CaseCalendarEventDto> events,
        ClientCase clientCase,
        IReadOnlySet<string> separateDeadlineTypes,
        CaseCalendarDateRange range)
    {
        AddCaseCalendarEvent(events, clientCase, "case-deadline", "due", "Крайний срок по делу", clientCase.DueDate, true, range);
        if (!separateDeadlineTypes.Contains("hearing"))
        {
            AddCaseCalendarEvent(events, clientCase, "court", "hearing", "Судебное заседание", clientCase.NextHearingAt, false, range);
        }

        AddCaseCalendarEvent(events, clientCase, "court", "claim", "Подача иска", clientCase.ClaimFiledAt, true, range);
        AddCaseCalendarEvent(events, clientCase, "court", "decision", "Судебное решение", clientCase.CourtDecisionAt, true, range);
        AddLegacyProceduralCalendarEvent(events, clientCase, separateDeadlineTypes, "appeal", "Срок апелляции", clientCase.AppealDeadlineAt, range);
        AddLegacyProceduralCalendarEvent(events, clientCase, separateDeadlineTypes, "cassation", "Срок кассации", clientCase.CassationDeadlineAt, range);
        AddLegacyProceduralCalendarEvent(events, clientCase, separateDeadlineTypes, "motion", "Срок подачи ходатайства", clientCase.MotionDeadlineAt, range);
        AddLegacyProceduralCalendarEvent(events, clientCase, separateDeadlineTypes, "objection", "Срок подачи возражений", clientCase.ObjectionDeadlineAt, range);
    }

    private static void AddLegacyProceduralCalendarEvent(
        ICollection<CaseCalendarEventDto> events,
        ClientCase clientCase,
        IReadOnlySet<string> separateDeadlineTypes,
        string sourceType,
        string title,
        DateTime? startAt,
        CaseCalendarDateRange range)
    {
        if (separateDeadlineTypes.Contains(sourceType))
        {
            return;
        }

        AddCaseCalendarEvent(events, clientCase, "procedural-deadline", sourceType, title, startAt, true, range);
    }

    private static void AddCaseCalendarProceduralDeadlineEvents(
        ICollection<CaseCalendarEventDto> events,
        ClientCase clientCase,
        CaseCalendarDateRange range)
    {
        foreach (var deadline in clientCase.ProceduralDeadlines
            .Where(item => item.Status != "closed" && item.Status != "cancelled" && item.Status != "archived")
            .Where(item => item.AllDay
                ? item.DueAt >= range.FromDate && item.DueAt < range.ToDateExclusive
                : item.DueAt >= range.FromInstantUtc && item.DueAt < range.ToInstantExclusiveUtc)
            .OrderBy(item => item.DueAt)
            .ThenBy(item => item.Id))
        {
            var normalizedStart = NormalizeCaseCalendarStart(deadline.DueAt, deadline.AllDay);
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
                IsOverdue = IsCalendarEventOverdue(normalizedStart, deadline.AllDay, range),
                IsWarning = IsCalendarEventInWarningPeriod(normalizedStart, deadline.AllDay, Math.Max(0, deadline.WarningDaysBefore), range),
                CourtName = NormalizeOptional(clientCase.CourtName),
                CourtCaseNumber = NormalizeOptional(clientCase.CourtCaseNumber),
            });
        }
    }

    private static void AddCaseCalendarCourtEvents(
        ICollection<CaseCalendarEventDto> events,
        ClientCase clientCase,
        CaseCalendarDateRange range)
    {
        foreach (var courtEvent in clientCase.CourtEvents
            .Where(item => item.MovementStatus != "cancelled" && item.MovementStatus != "archived")
            .Where(item => item.AllDay
                ? item.ScheduledAt >= range.FromDate && item.ScheduledAt < range.ToDateExclusive
                : item.ScheduledAt >= range.FromInstantUtc && item.ScheduledAt < range.ToInstantExclusiveUtc)
            .OrderBy(item => item.ScheduledAt)
            .ThenBy(item => item.Id))
        {
            var normalizedStart = NormalizeCaseCalendarStart(courtEvent.ScheduledAt, courtEvent.AllDay);
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
                IsOverdue = IsCalendarEventOverdue(normalizedStart, courtEvent.AllDay, range) && courtEvent.MovementStatus is not "held" and not "completed",
                IsWarning = IsCalendarEventInWarningPeriod(normalizedStart, courtEvent.AllDay, 7, range),
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
        CaseCalendarDateRange range)
    {
        var rangeStart = allDay ? range.FromDate : range.FromInstantUtc;
        var rangeEnd = allDay ? range.ToDateExclusive : range.ToInstantExclusiveUtc;
        if (!startAt.HasValue || startAt.Value < rangeStart || startAt.Value >= rangeEnd)
        {
            return;
        }

        var normalizedStart = NormalizeCaseCalendarStart(startAt.Value, allDay);
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
            IsOverdue = kind.Contains("deadline", StringComparison.OrdinalIgnoreCase) && IsCalendarEventOverdue(normalizedStart, allDay, range),
            IsWarning = kind.Contains("deadline", StringComparison.OrdinalIgnoreCase) && IsCalendarEventInWarningPeriod(normalizedStart, allDay, 7, range),
            CourtName = NormalizeOptional(clientCase.CourtName),
            CourtCaseNumber = NormalizeOptional(clientCase.CourtCaseNumber),
        });
    }

    private static CaseCalendarEventDto MapTaskCalendarEvent(ClientCase clientCase, TaskJournalItem task, CaseCalendarDateRange range)
    {
        var durationMinutes = task.PlannedDurationMinutes > 0 ? task.PlannedDurationMinutes : 60;
        var normalizedStart = NormalizeCaseCalendarStart(task.DueAt, allDay: false);
        return new CaseCalendarEventDto
        {
            Id = $"task:{task.Id}",
            Kind = "task",
            SourceType = task.Type,
            SourceId = task.Id,
            Title = task.Title,
            StartAt = normalizedStart,
            EndAt = normalizedStart.AddMinutes(durationMinutes),
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
            IsOverdue = normalizedStart < range.NowInstantUtc
                && !string.Equals(task.Status, "done", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(task.Status, "cancelled", StringComparison.OrdinalIgnoreCase),
            IsWarning = normalizedStart >= range.NowInstantUtc && normalizedStart <= range.NowInstantUtc.AddDays(7),
            CourtName = NormalizeOptional(clientCase.CourtName),
            CourtCaseNumber = NormalizeOptional(clientCase.CourtCaseNumber),
        };
    }

    private static DateTime NormalizeCaseCalendarStart(DateTime value, bool allDay)
        => allDay
            ? value.Date
            : CalendarTimeZoneHelper.NormalizeUtcInstant(value);

    private static bool IsCalendarEventOverdue(DateTime startAt, bool allDay, CaseCalendarDateRange range)
        => allDay
            ? startAt.Date < range.TodayDate
            : startAt < range.NowInstantUtc;

    private static bool IsCalendarEventInWarningPeriod(DateTime startAt, bool allDay, int warningDays, CaseCalendarDateRange range)
    {
        var normalizedWarningDays = Math.Max(0, warningDays);
        return allDay
            ? startAt.Date >= range.TodayDate && startAt.Date <= range.TodayDate.AddDays(normalizedWarningDays)
            : startAt >= range.NowInstantUtc && startAt <= range.NowInstantUtc.AddDays(normalizedWarningDays);
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
        var resetsInactivityTimer = responsibleUserId.HasValue && responsibleUserId == userId;
        if (resetsInactivityTimer)
        {
            clientCase.LastViewedAt = now;
            clientCase.LastViewedByUserId = userId;
            clientCase.LastViewedByUserName = displayName;
            PopulateResponsibleViewAliases([clientCase]);
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
            LastViewedByResponsibleAt = clientCase.LastViewedAt,
            LastViewedByResponsibleUserId = clientCase.LastViewedByUserId,
            LastViewedByResponsibleUserName = clientCase.LastViewedByUserName,
            ActivityConfirmedAt = clientCase.ActivityConfirmedAt,
            ActivityConfirmedByUserId = clientCase.ActivityConfirmedByUserId,
            ActivityConfirmedByUserName = clientCase.ActivityConfirmedByUserName,
            InactivityRemindersEnabled = clientCase.InactivityRemindersEnabled,
            InactivityRemindersDisabledReason = clientCase.InactivityRemindersDisabledReason,
            EventHistory = clientCase.EventHistory,
        });
    }

    [HttpPost("{id:int}/history")]
    public async Task<ActionResult<ClientCaseActivityDto>> AddHistoryEntry(
        int id,
        [FromBody] AddClientCaseHistoryEntryRequest request,
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
            LastViewedByResponsibleAt = clientCase.LastViewedAt,
            LastViewedByResponsibleUserId = clientCase.LastViewedByUserId,
            LastViewedByResponsibleUserName = clientCase.LastViewedByUserName,
            ActivityConfirmedAt = clientCase.ActivityConfirmedAt,
            ActivityConfirmedByUserId = clientCase.ActivityConfirmedByUserId,
            ActivityConfirmedByUserName = clientCase.ActivityConfirmedByUserName,
            InactivityRemindersEnabled = clientCase.InactivityRemindersEnabled,
            InactivityRemindersDisabledReason = clientCase.InactivityRemindersDisabledReason,
            EventHistory = clientCase.EventHistory,
        });
    }

    [HttpPost("{id:int}/conversation")]
    [Consumes("application/json")]
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

        var messageResult = NormalizeConversationMessage(request.Text);
        if (messageResult.Error is not null)
        {
            return BadRequest(new { message = messageResult.Error });
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

        return await AddConversationEntryCore(clientCase, messageResult.Text!, [], ct);
    }

    [HttpPost("{id:int}/conversation")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxCaseDocumentBytes)]
    public async Task<ActionResult<IReadOnlyList<ClientCaseConversationEntryDto>>> AddConversationEntryWithAttachments(
        int id,
        [FromForm] AddClientCaseConversationEntryFormRequest request,
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

        var messageResult = NormalizeConversationMessage(request.Text);
        if (messageResult.Error is not null)
        {
            return BadRequest(new { message = messageResult.Error });
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

        return await AddConversationEntryCore(clientCase, messageResult.Text!, request.Files ?? [], ct);
    }

    [HttpGet("{id:int}/related-parties")]
    public async Task<ActionResult<IReadOnlyList<ClientCaseRelatedPartyDto>>> GetRelatedParties(
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

        var canRevealSensitiveData = await CanRevealCaseSensitiveDataAsync(ct);

        return Ok(items.Select(item => MapRelatedParty(item, canRevealSensitiveData)).ToList());
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

        var workOrder = await _db.ClientCaseWorkOrders
            .AsNoTracking()
            .Include(item => item.Items)
            .FirstOrDefaultAsync(item => item.ClientCaseId == id, ct);

        return Ok(workOrder is null
            ? ClientCaseWorkOrderDto.Empty(id)
            : MapWorkOrder(workOrder));
    }

    [HttpGet("{id:int}/work-order/external-approval")]
    public async Task<ActionResult<ExternalWorkOrderApprovalStatusDto?>> GetExternalWorkOrderApproval(
        int id,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        if (!await CanAccessCaseAsync(id, ct))
        {
            return NotFound();
        }

        if (_externalWorkOrderApprovalService is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "Модуль внешнего согласования недоступен." });
        }

        try
        {
            var status = await _externalWorkOrderApprovalService.GetLatestStatusAsync(id, ct);
            return status is null ? NoContent() : Ok(status);
        }
        catch (ExternalWorkOrderApprovalException ex)
        {
            return MapExternalWorkOrderApprovalError(ex);
        }
    }

    [HttpPost("{id:int}/work-order/external-approval")]
    public async Task<ActionResult<ExternalWorkOrderApprovalStatusDto>> CreateExternalWorkOrderApproval(
        int id,
        [FromBody] CreateExternalWorkOrderApprovalRequest? request,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        if (!await CanAccessCaseAsync(id, ct))
        {
            return NotFound();
        }

        if (_externalWorkOrderApprovalService is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "Модуль внешнего согласования недоступен." });
        }

        try
        {
            var status = await _externalWorkOrderApprovalService.CreateAsync(
                id,
                request ?? new CreateExternalWorkOrderApprovalRequest(),
                User.GetUserId(),
                User.GetDisplayName(),
                ct);
            return Ok(status);
        }
        catch (ExternalWorkOrderApprovalException ex)
        {
            return MapExternalWorkOrderApprovalError(ex);
        }
    }

    [HttpDelete("{id:int}/work-order/external-approval")]
    public async Task<IActionResult> RevokeExternalWorkOrderApproval(int id, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        if (!await CanAccessCaseAsync(id, ct))
        {
            return NotFound();
        }

        if (_externalWorkOrderApprovalService is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "Модуль внешнего согласования недоступен." });
        }

        try
        {
            return await _externalWorkOrderApprovalService.RevokeAsync(
                id,
                User.GetUserId(),
                User.GetDisplayName(),
                ct)
                ? NoContent()
                : NotFound(new { message = "Активный запрос внешнего согласования не найден." });
        }
        catch (ExternalWorkOrderApprovalException ex)
        {
            return MapExternalWorkOrderApprovalError(ex);
        }
    }

    private async Task<bool> CanAccessCaseAsync(int id, CancellationToken ct)
    {
        var visibility = await ResolveCasesVisibilityAsync(ct);
        return visibility is not null && await _db.ClientCases
            .AsNoTracking()
            .ApplyClientCaseVisibility(visibility)
            .AnyAsync(item => item.Id == id, ct);
    }

    private ActionResult MapExternalWorkOrderApprovalError(ExternalWorkOrderApprovalException ex)
        => ex.Code switch
        {
            "feature_disabled" => StatusCode(StatusCodes.Status503ServiceUnavailable, new { ex.Code, message = ex.Message }),
            "work_order_not_found" => NotFound(new { ex.Code, message = ex.Message }),
            "validation_error" or "work_order_empty" => BadRequest(new { ex.Code, message = ex.Message }),
            _ => Conflict(new { ex.Code, message = ex.Message }),
        };

    [HttpGet("{id:int}/work-order/print")]
    public async Task<IActionResult> PrintWorkOrder(int id, CancellationToken ct)
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

        var statusClock = await ResolveCaseStatusClockAsync(ct);
        return Content(BuildWorkOrderPrintHtml(clientCase, workOrder, statusClock.NowLocal), "text/html; charset=utf-8", Encoding.UTF8);
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
            BuildWorkOrderPrintHtml(clientCase, workOrder, (await ResolveCaseStatusClockAsync(ct)).NowLocal),
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
        if (await _db.ExternalWorkOrderApprovalRequests
            .AnyAsync(item => item.ClientCaseWorkOrderId == workOrder.Id, ct))
        {
            return BadRequest(new { message = "Заказ-наряд с историей внешнего согласования нельзя удалить. Создайте новую версию или отзовите активную ссылку." });
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

        if (await RequireSectionPermissionAsync(RolePermissionCatalog.Accounts, static permission => permission.CanCreate, ct) is { } accountsForbidden)
        {
            return accountsForbidden;
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

        var statusClock = await ResolveCaseStatusClockAsync(ct);
        var invoiceDate = request?.Date?.Date ?? statusClock.TodayDate;
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
            BuildWorkOrderInvoiceDocumentHtml(clientCase, workOrder, payment, number, invoiceDate, dueDate, statusClock.NowLocal),
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

    [HttpPost("{id:int}/work-order/create-act")]
    public async Task<ActionResult<ClientCaseWorkOrderActDto>> CreateActFromWorkOrder(
        int id,
        [FromBody] CreateClientCaseWorkOrderActRequest? request,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        if (await RequireSectionPermissionAsync(RolePermissionCatalog.Acts, static permission => permission.CanCreate, ct) is { } actsForbidden)
        {
            return actsForbidden;
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
            return BadRequest(new { message = "Добавьте услуги с ненулевой стоимостью перед созданием акта." });
        }

        var workOrderStatus = NormalizeWorkOrderStatus(workOrder.Status);
        if (workOrderStatus is not ("approved" or "invoice_created"))
        {
            return BadRequest(new { message = "Перед созданием акта согласуйте заказ-наряд." });
        }

        var candidateItems = workOrder.Items
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Id)
            .ToList();
        var candidateSourceKeys = candidateItems
            .Select(item => BuildWorkOrderActLineSourceKey(workOrder.Id, item.Id))
            .ToList();
        var usedSourceKeys = await _db.Set<ActLineItem>()
            .AsNoTracking()
            .Where(item => candidateSourceKeys.Contains(item.SourceKey)
                && item.Act.WorkflowStatus != ActWorkflowStatus.Cancelled
                && item.Act.Status != ActStatus.Terminated)
            .Select(item => item.SourceKey)
            .ToListAsync(ct);
        var usedSourceKeySet = usedSourceKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actItems = candidateItems
            .Where(item => !usedSourceKeySet.Contains(BuildWorkOrderActLineSourceKey(workOrder.Id, item.Id)))
            .ToList();
        if (actItems.Count == 0)
        {
            return BadRequest(new { message = "Все услуги заказ-наряда уже актированы." });
        }

        var statusClock = await ResolveCaseStatusClockAsync(ct);
        var actDate = request?.Date?.Date ?? statusClock.TodayDate;
        var number = await BuildWorkOrderActNumberAsync(id, workOrder.Id, actDate, request?.Number, ct);
        var amount = decimal.Round(actItems.Sum(item => item.Quantity * item.UnitPrice), 2, MidpointRounding.AwayFromZero);
        var now = DateTime.UtcNow;
        var act = new Act
        {
            Number = number,
            Title = NormalizeOptional(request?.Title) ?? $"Акт по заказ-наряду «{workOrder.Title}»",
            Date = actDate,
            Amount = amount,
            CreationMode = ActCreationMode.Manual,
            Status = ActStatus.Created,
            WorkflowStatus = ActWorkflowStatus.Draft,
            ClientId = clientCase.ClientId,
            LegalEntityId = clientCase.LegalEntityId,
            ResponsibleId = actItems.FirstOrDefault(item => item.ExecutorUserId.HasValue)?.ExecutorUserId,
            Comment = NormalizeOptional(request?.Comment)
                ?? $"Сформирован из заказ-наряда «{workOrder.Title}» по делу {clientCase.Title}.",
            CreatedAt = now,
        };

        var sortOrder = 1;
        foreach (var item in actItems)
        {
            act.LineItems.Add(new ActLineItem
            {
                SourceKey = BuildWorkOrderActLineSourceKey(workOrder.Id, item.Id),
                SourceSortOrder = item.SortOrder,
                Name = item.ServiceName,
                Quantity = item.Quantity,
                Unit = "шт",
                Price = item.UnitPrice,
                SortOrder = sortOrder++,
                CreatedAt = now,
            });
        }

        _db.Acts.Add(act);
        _db.SetAuditReason($"По предварительному расчету дела #{id} создан акт №{number}.");
        AppendCaseEvent(clientCase, $"По предварительному расчету «{workOrder.Title}» создан акт №{number} на сумму {amount:0.##} {workOrder.Currency}. Создал: {User.GetDisplayName()}.");
        await _db.SaveChangesAsync(ct);

        var actDocument = await SaveGeneratedCaseDocumentAsync(
            clientCase,
            BuildWorkOrderActDocumentKey(act.Id),
            "Акт",
            "Черновик",
            $"case-{id}-act-{act.Id}.html",
            BuildWorkOrderActDocumentHtml(clientCase, workOrder, act, statusClock.NowLocal),
            "Generated",
            $"Сформирован документ акта №{number} из заказ-наряда «{workOrder.Title}».",
            $"Акт №{number} из заказ-наряда «{workOrder.Title}» сохранен в документы дела. Автор: {User.GetDisplayName()}.",
            ct);

        return Created($"/acts/{act.Id}", new ClientCaseWorkOrderActDto
        {
            ActId = act.Id,
            Number = number,
            Date = actDate,
            Amount = act.Amount,
            DocumentId = actDocument.Id,
            LineItemCount = act.LineItems.Count,
            WorkOrder = MapWorkOrder(workOrder),
        });
    }

    [HttpPost("{id:int}/work-order/create-contract")]
    public async Task<ActionResult<ClientCaseWorkOrderContractDto>> CreateContractFromWorkOrder(
        int id,
        [FromBody] CreateClientCaseWorkOrderContractRequest? request,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        if (await RequireSectionPermissionAsync(RolePermissionCatalog.Contracts, static permission => permission.CanCreate, ct) is { } contractsForbidden)
        {
            return contractsForbidden;
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
            return BadRequest(new { message = "Добавьте услуги с ненулевой стоимостью перед созданием договора." });
        }

        var workOrderStatus = NormalizeWorkOrderStatus(workOrder.Status);
        if (workOrderStatus is not ("approved" or "invoice_created"))
        {
            return BadRequest(new { message = "Перед созданием договора согласуйте заказ-наряд." });
        }

        if (!clientCase.ClientId.HasValue && !clientCase.LegalEntityId.HasValue)
        {
            return BadRequest(new { message = "Для создания договора у дела должен быть указан клиент или юридическое лицо." });
        }

        var statusClock = await ResolveCaseStatusClockAsync(ct);
        var contractDate = request?.Date?.Date ?? statusClock.TodayDate;
        var startDate = request?.StartDate?.Date ?? contractDate;
        var number = await BuildWorkOrderContractNumberAsync(id, workOrder.Id, contractDate, request?.Number, ct);
        var items = workOrder.Items
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Id)
            .ToList();
        var description = NormalizeOptional(request?.Description)
            ?? BuildWorkOrderContractDescription(clientCase, workOrder, items);
        var now = DateTime.UtcNow;
        var isOpenEnded = request?.IsOpenEnded ?? (request?.ValidUntil is null);
        var contract = new Contract
        {
            Number = number,
            Title = NormalizeOptional(request?.Title) ?? $"Договор по заказ-наряду «{workOrder.Title}»",
            Date = contractDate,
            StartDate = startDate,
            ValidUntil = request?.ValidUntil?.Date,
            Description = description,
            BillingMode = ContractBillingMode.Fixed,
            Status = ContractStatus.Draft,
            IsOpenEnded = isOpenEnded,
            Amount = workOrder.TotalAmount,
            CreatedAt = now,
        };

        if (clientCase.ClientId.HasValue)
        {
            contract.ClientContracts.Add(new ClientContract
            {
                ClientId = clientCase.ClientId.Value,
                Comment = $"Создан из заказ-наряда «{workOrder.Title}» по делу {clientCase.Title}.",
            });
        }

        if (clientCase.LegalEntityId.HasValue)
        {
            contract.LegalEntityContracts.Add(new LegalEntityContract
            {
                LegalEntityId = clientCase.LegalEntityId.Value,
                Comment = $"Создан из заказ-наряда «{workOrder.Title}» по делу {clientCase.Title}.",
            });
        }

        _db.Contracts.Add(contract);
        _db.SetAuditReason($"По предварительному расчету дела #{id} создан договор №{number}.");
        AppendCaseEvent(clientCase, $"По предварительному расчету «{workOrder.Title}» создан договор №{number} на сумму {workOrder.TotalAmount:0.##} {workOrder.Currency}. Создал: {User.GetDisplayName()}.");
        await _db.SaveChangesAsync(ct);

        var contractDocument = await SaveGeneratedCaseDocumentAsync(
            clientCase,
            BuildWorkOrderContractDocumentKey(contract.Id),
            "Договор",
            "Черновик",
            $"case-{id}-contract-{contract.Id}.html",
            BuildWorkOrderContractDocumentHtml(clientCase, workOrder, contract, items, statusClock.NowLocal),
            "Generated",
            $"Сформирован документ договора №{number} из заказ-наряда «{workOrder.Title}».",
            $"Договор №{number} из заказ-наряда «{workOrder.Title}» сохранен в документы дела. Автор: {User.GetDisplayName()}.",
            ct);

        return Created($"/contracts/{contract.Id}", new ClientCaseWorkOrderContractDto
        {
            ContractId = contract.Id,
            Number = number,
            Date = contractDate,
            StartDate = startDate,
            Amount = contract.Amount,
            DocumentId = contractDocument.Id,
            ClientId = clientCase.ClientId,
            LegalEntityId = clientCase.LegalEntityId,
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

        var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct);
        var logs = SplitCaseHistory(clientCase.EventHistory)
            .Where(IsCourtSyncHistoryLine)
            .Select((line, index) => MapCourtSyncLog(clientCase.Id, line, index, timeZone))
            .OrderByDescending(item => item.OccurredAt ?? DateTime.MinValue)
            .ThenByDescending(item => item.Id)
            .ToList();

        return Ok(logs);
    }

    [HttpGet("court-sync/logs")]
    public async Task<ActionResult<IReadOnlyList<ClientCaseCourtSyncJournalEntryDto>>> GetCourtSyncJournal(
        [FromQuery] int take = 50,
        [FromQuery] string? status = null,
        [FromQuery] string? search = null,
        CancellationToken ct = default,
        [FromQuery] string? provider = null,
        [FromQuery] bool? hasError = null,
        [FromQuery] int? maxAgeDays = null,
        [FromQuery] int? minAgeDays = null)
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

        var limit = Math.Clamp(take, 1, 200);
        var statusFilter = NormalizeOptional(status);
        var searchFilter = NormalizeOptional(search);
        var providerFilter = NormalizeOptional(provider);
        var normalizedMaxAgeDays = maxAgeDays.HasValue ? Math.Clamp(maxAgeDays.Value, 0, 3650) : (int?)null;
        var normalizedMinAgeDays = minAgeDays.HasValue ? Math.Clamp(minAgeDays.Value, 0, 3650) : (int?)null;
        var utcNow = DateTime.UtcNow;

        var caseQuery = _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .AsNoTracking()
            .Where(item => item.EventHistory != null && item.EventHistory != string.Empty);

        if (!string.IsNullOrWhiteSpace(searchFilter))
        {
            var normalizedSearch = searchFilter.ToLower();
            caseQuery = caseQuery.Where(item =>
                item.Title.ToLower().Contains(normalizedSearch)
                || item.CaseNumber.ToLower().Contains(normalizedSearch)
                || item.CourtName.ToLower().Contains(normalizedSearch)
                || item.CourtCaseNumber.ToLower().Contains(normalizedSearch)
                || item.CourtUrl.ToLower().Contains(normalizedSearch)
                || item.EventHistory.ToLower().Contains(normalizedSearch)
                || (item.Client != null && item.Client.Name.ToLower().Contains(normalizedSearch))
                || (item.LegalEntity != null
                    && ((item.LegalEntity.ShortName != null && item.LegalEntity.ShortName.ToLower().Contains(normalizedSearch))
                        || (item.LegalEntity.FullName != null && item.LegalEntity.FullName.ToLower().Contains(normalizedSearch)))));
        }

        if (string.Equals(statusFilter, "external-sync-error", StringComparison.OrdinalIgnoreCase)
            || hasError == true)
        {
            caseQuery = caseQuery.Where(item => item.EventHistory.Contains("не выполнена"));
        }
        else if (string.Equals(statusFilter, "manual-check-recorded", StringComparison.OrdinalIgnoreCase))
        {
            caseQuery = caseQuery.Where(item => item.EventHistory.Contains("зафиксирована"));
        }

        var caseRows = await caseQuery
            .OrderByDescending(item => item.Id)
            .Take(5000)
            .Select(item => new CourtSyncJournalCaseRow(
                item.Id,
                item.Title,
                item.CaseNumber,
                item.Client != null
                    ? item.Client.Name
                    : item.LegalEntity != null
                        ? ((item.LegalEntity.ShortName != null && item.LegalEntity.ShortName != string.Empty)
                            ? item.LegalEntity.ShortName
                            : item.LegalEntity.FullName ?? string.Empty)
                        : string.Empty,
                item.CourtName,
                item.CourtCaseNumber,
                item.CourtUrl,
                item.EventHistory))
            .ToListAsync(ct);

        var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct);
        var entries = caseRows
            .SelectMany(row =>
            {
                var providerInfo = ResolveCourtSyncJournalProvider(row.CourtUrl);
                return SplitCaseHistory(row.EventHistory)
                    .Select((line, index) => new { row, line, index })
                    .Where(item => IsCourtSyncHistoryLine(item.line))
                    .Select(item => MapCourtSyncJournalEntry(item.row, item.line, item.index, timeZone, providerInfo, utcNow));
            })
            .Where(item => string.IsNullOrWhiteSpace(statusFilter) || string.Equals(item.Status, statusFilter, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(searchFilter) || CourtSyncJournalEntryMatches(item, searchFilter))
            .Where(item => string.IsNullOrWhiteSpace(providerFilter) || string.Equals(item.ProviderKey, providerFilter, StringComparison.OrdinalIgnoreCase))
            .Where(item => !hasError.HasValue || item.HasError == hasError.Value)
            .Where(item => !normalizedMaxAgeDays.HasValue
                || (item.OccurredAt.HasValue && item.OccurredAt.Value >= utcNow.AddDays(-normalizedMaxAgeDays.Value)))
            .Where(item => !normalizedMinAgeDays.HasValue
                || (item.OccurredAt.HasValue && item.OccurredAt.Value < utcNow.AddDays(-normalizedMinAgeDays.Value)))
            .OrderByDescending(item => item.OccurredAt ?? DateTime.MinValue)
            .ThenByDescending(item => item.ClientCaseId)
            .ThenByDescending(item => item.LogId)
            .Take(limit)
            .ToList();

        return Ok(entries);
    }

    [HttpGet("court-sync/settings")]
    public async Task<ActionResult<ClientCaseCourtSyncSettingsDto>> GetCourtSyncSettings(CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        return Ok(await ResolveCourtSyncSettingsAsync(ct));
    }

    [HttpGet("court-sync/providers")]
    public async Task<ActionResult<IReadOnlyList<ClientCaseCourtSyncProviderDto>>> GetCourtSyncProviders(CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var externalCourtSyncEnabled = _featureFlags?.CurrentValue.Legal.ExternalCourtSync ?? false;
        var providers = _courtSyncProviderRegistry?.GetProviders()
            ?? CourtSyncProviderRegistry.GetDefaultDescriptors(externalCourtSyncEnabled);

        return Ok(providers
            .Select(static provider => new ClientCaseCourtSyncProviderDto
            {
                Key = provider.Key,
                Label = provider.Label,
                SourceType = provider.SourceType,
                SchemaVersion = provider.SchemaVersion,
                Capabilities = provider.Capabilities,
                Status = provider.Status,
                IsAvailable = provider.IsAvailable,
                FeatureFlagEnabled = provider.FeatureFlagEnabled,
                SupportsManualRun = provider.SupportsManualRun,
                SupportsBackgroundSync = provider.SupportsBackgroundSync,
                SupportedHosts = provider.SupportedHosts,
                Message = provider.Message,
            })
            .ToList());
    }

    [HttpPost("{id:int}/court-sync/search")]
    public async Task<ActionResult<IReadOnlyList<ClientCaseCourtSyncSearchResultDto>>> SearchCourtSyncCases(
        int id,
        [FromBody] ClientCaseCourtSyncSearchRequest? request,
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
            .AsNoTracking()
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var normalizedInstance = NormalizeCourtInstance(request?.Instance);
        var currentCourtInstance = await _db.ClientCaseCourtInstances
            .AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.ClientCaseId == id && item.Instance == normalizedInstance,
                ct);
        var sourceUrl = NormalizeOptional(request?.SourceUrl) ?? NormalizeOptional(clientCase.CourtUrl);
        if (sourceUrl is null)
        {
            return BadRequest(new { message = "Укажите ссылку на судебную карточку или страницу поиска суда." });
        }

        var provider = ResolveCourtSyncProvider(sourceUrl);
        if (provider is null)
        {
            return BadRequest(new { message = "Для указанного источника пока нет активного провайдера судебной синхронизации." });
        }
        if (provider is not ICourtSyncCaseSearchProvider searchProvider)
        {
            return BadRequest(new { message = "Выбранный судебный источник не поддерживает поиск карточек." });
        }

        try
        {
            var results = await searchProvider.SearchCasesAsync(
                new CourtSyncCaseSearchRequest(
                    sourceUrl,
                    NormalizeOptional(request?.CourtCaseNumber) ?? NormalizeOptional(clientCase.CourtCaseNumber),
                    NormalizeOptional(request?.JudicialUid),
                    NormalizeOptional(request?.CourtName) ?? NormalizeOptional(clientCase.CourtName),
                    NormalizeOptional(request?.PartyName),
                    normalizedInstance),
                ct);

            return Ok(results.Select(item => MapCourtSyncSearchResult(clientCase, currentCourtInstance, item)).ToList());
        }
        catch (SudrfCourtSyncException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("{id:int}/court-sync/bind")]
    public async Task<ActionResult<ClientCaseCourtSyncBindingDto>> BindCourtSyncCase(
        int id,
        [FromBody] ClientCaseCourtSyncBindRequest? request,
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

        var sourceUrl = NormalizeOptional(request?.SourceUrl);
        if (sourceUrl is null)
        {
            return BadRequest(new { message = "Выберите судебную карточку или укажите ссылку на источник." });
        }

        var provider = ResolveCourtSyncProvider(sourceUrl);
        if (provider is null)
        {
            return BadRequest(new { message = "Для указанного источника пока нет активного провайдера судебной синхронизации." });
        }

        var normalizedInstance = NormalizeCourtInstance(request?.Instance);
        var courtInstance = await _db.ClientCaseCourtInstances
            .Include(item => item.Complaints.Where(complaint => !complaint.IsArchived))
            .FirstOrDefaultAsync(item => item.ClientCaseId == id && item.Instance == normalizedInstance, ct);
        var previewDifferences = BuildCourtSyncPreviewDifferences(
            clientCase,
            courtInstance,
            request?.CourtName,
            NormalizeOptional(request?.CourtCaseNumber) ?? NormalizeOptional(request?.JudicialUid),
            request?.JudgeName,
            sourceUrl);
        if (previewDifferences.Any(static item => item.Kind == "replace") && request?.ConfirmDifferences != true)
        {
            return Conflict(new
            {
                message = "Найденная судебная карточка отличается от уже заполненных данных дела. Проверьте изменения и подтвердите замену.",
                requiresConfirmation = true,
                differences = previewDifferences,
            });
        }

        var actorUserId = User.GetUserId();
        var actorName = User.GetDisplayName();
        var now = DateTime.UtcNow;
        var courtName = Truncate(NormalizeOptional(request?.CourtName) ?? NormalizeOptional(clientCase.CourtName) ?? string.Empty, 200);
        var courtCaseNumber = Truncate(
            NormalizeOptional(request?.CourtCaseNumber)
                ?? NormalizeOptional(request?.JudicialUid)
                ?? NormalizeOptional(clientCase.CourtCaseNumber)
                ?? string.Empty,
            100);
        var judicialUid = NormalizeOptional(request?.JudicialUid);

        clientCase.CourtName = courtName;
        clientCase.CourtCaseNumber = courtCaseNumber;
        clientCase.CourtUrl = Truncate(sourceUrl, 1000);

        if (courtInstance is null)
        {
            courtInstance = new ClientCaseCourtInstance
            {
                ClientCaseId = id,
                Instance = normalizedInstance,
                CreatedAt = now,
                CreatedByUserId = actorUserId,
                CreatedByUserName = Truncate(actorName, 256),
            };
            _db.ClientCaseCourtInstances.Add(courtInstance);
        }

        courtInstance.CourtName = courtName;
        courtInstance.CourtCaseNumber = courtCaseNumber;
        courtInstance.CourtUrl = clientCase.CourtUrl;
        if (judicialUid is not null)
        {
            var externalIdNote = $"Внешний идентификатор: {Truncate(judicialUid, 120)}";
            if (!courtInstance.Notes.Contains(externalIdNote, StringComparison.OrdinalIgnoreCase))
            {
                courtInstance.Notes = string.IsNullOrWhiteSpace(courtInstance.Notes)
                    ? externalIdNote
                    : Truncate($"{courtInstance.Notes.Trim()}{Environment.NewLine}{externalIdNote}", 1000);
            }
        }
        courtInstance.UpdatedAt = now;
        courtInstance.UpdatedByUserId = actorUserId;
        courtInstance.UpdatedByUserName = Truncate(actorName, 256);

        CourtSyncApplyResult? syncResult = null;
        string? syncError = null;
        try
        {
            if (provider is not ICourtSyncCaseReader caseReader)
            {
                return BadRequest(new { message = "Выбранный судебный источник не поддерживает чтение карточки дела." });
            }

            var courtData = await caseReader.ReadCaseAsync(
                new CourtSyncCaseBinding(clientCase.CourtUrl, courtCaseNumber, judicialUid, normalizedInstance),
                ct);
            var sourceTimeZone = await ResolveCourtSyncSourceTimeZoneAsync(clientCase, ct);
            syncResult = await ApplySudrfCourtSyncAsync(clientCase, courtData, sourceTimeZone, actorName, now, ct);
        }
        catch (SudrfCourtSyncException ex)
        {
            syncError = ex.Message;
            await NotifyCourtSyncFailedForCaseAsync(
                clientCase,
                provider.Label,
                $"Автоматическая синхронизация после привязки не выполнена: {ex.Message}",
                actorUserId,
                actorName,
                ct);
        }

        var externalIdSuffix = judicialUid is null
            ? string.Empty
            : $" Внешний идентификатор: {Truncate(judicialUid, 120)}.";
        var syncMessage = syncResult.HasValue
            ? !syncResult.Value.HasChanges
                ? "Судебный источник проверен: новых изменений не найдено."
                : $"Судебный источник обновлен: {FormatCourtSyncResult(syncResult.Value)}"
            : $"Автоматическая синхронизация после привязки не выполнена: {syncError}";
        AppendCaseEvent(clientCase, $"Привязана судебная карточка «{ResolveCourtInstanceLabel(normalizedInstance)}» через {provider.Label}. Суд: {FormatCaseTextValue(courtName)}; № в суде: {FormatCaseTextValue(courtCaseNumber)}.{externalIdSuffix} {syncMessage} Автор: {actorName}.");
        _db.SetAuditReason($"Привязана судебная карточка дела через {provider.Label}.");
        await _db.SaveChangesAsync(ct);
        if (syncResult is { RequiresDeadlineTaskSync: true })
        {
            await ClientCaseDeadlineTaskSyncService.SyncAsync(_db, clientCase.Id, actorUserId, actorName, ct);
        }

        return Ok(new ClientCaseCourtSyncBindingDto
        {
            ClientCaseId = clientCase.Id,
            Instance = courtInstance.Instance,
            ProviderKey = provider.Key,
            SourceType = provider.SourceType,
            CourtName = clientCase.CourtName,
            CourtCaseNumber = clientCase.CourtCaseNumber,
            CourtUrl = clientCase.CourtUrl,
            JudicialUid = judicialUid ?? string.Empty,
            Message = syncResult.HasValue
                ? $"Судебная карточка привязана к делу. {syncMessage}"
                : $"Судебная карточка привязана к делу, но синхронизация не выполнена: {syncError}",
            SyncedFromExternalSource = syncResult.HasValue,
            UpdatedFields = syncResult?.ChangedFields ?? [],
            CreatedCourtEvents = syncResult?.CreatedCourtEvents ?? 0,
            UpdatedCourtEvents = syncResult?.UpdatedCourtEvents ?? 0,
            CreatedCourtParties = syncResult?.CreatedCourtParties ?? 0,
            CreatedCourtActDocuments = syncResult?.CreatedCourtActDocuments ?? 0,
            CreatedProceduralDeadlines = syncResult?.CreatedProceduralDeadlines ?? 0,
            NewItems = syncResult?.NewItems ?? [],
            ChangedItems = syncResult?.ChangedItems ?? [],
            RemovedItems = syncResult?.RemovedItems ?? [],
            ConflictItems = syncResult?.ConflictItems ?? [],
            SyncError = syncError ?? string.Empty,
            CourtInstance = MapCourtInstance(courtInstance),
        });
    }

    [HttpPut("court-sync/settings")]
    public async Task<ActionResult<ClientCaseCourtSyncSettingsDto>> UpdateCourtSyncSettings(
        UpdateClientCaseCourtSyncSettingsRequest request,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var intervalMinutes = ClientCaseCourtSyncSchedulerService.NormalizeIntervalMinutes(
            request.IntervalMinutes ?? ClientCaseCourtSyncSchedulerService.DefaultIntervalMinutes);
        var batchSize = ClientCaseCourtSyncSchedulerService.NormalizeBatchSize(
            request.BatchSize ?? ClientCaseCourtSyncSchedulerService.DefaultBatchSize);
        var now = DateTime.UtcNow;

        await UpsertSystemSettingAsync(ClientCaseCourtSyncSchedulerService.EnabledSettingKey, request.IsEnabled ? "true" : "false", now, ct);
        await UpsertSystemSettingAsync(ClientCaseCourtSyncSchedulerService.IntervalMinutesSettingKey, intervalMinutes.ToString(CultureInfo.InvariantCulture), now, ct);
        await UpsertSystemSettingAsync(ClientCaseCourtSyncSchedulerService.BatchSizeSettingKey, batchSize.ToString(CultureInfo.InvariantCulture), now, ct);
        await _db.SaveChangesAsync(ct);

        return Ok(await ResolveCourtSyncSettingsAsync(ct));
    }

    [HttpPost("{id:int}/court-sync/run")]
    public async Task<ActionResult<ClientCaseCourtSyncRunDto>> RunCourtSync(
        int id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ClientCaseCourtSyncRunRequest? request,
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

        var actorName = User.GetDisplayName();
        var checkedAt = DateTime.UtcNow;
        var hasCourtBinding =
            !string.IsNullOrWhiteSpace(clientCase.CourtCaseNumber)
            || !string.IsNullOrWhiteSpace(clientCase.CourtUrl);
        var manualOnly = request?.ManualOnly == true;
        var manualComment = NormalizeOptional(request?.Comment);
        manualComment = manualComment is null ? string.Empty : Truncate(NormalizeHistoryText(manualComment), 1000);
        manualComment = manualComment.TrimEnd('.', ';', ' ');
        var manualCommentSuffix = string.IsNullOrWhiteSpace(manualComment)
            ? string.Empty
            : $" Комментарий: {manualComment}.";
        if (!hasCourtBinding && !manualOnly)
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

        var courtSyncProvider = manualOnly ? null : ResolveCourtSyncProvider(clientCase.CourtUrl);
        if (courtSyncProvider is ICourtSyncCaseReader caseReader && !string.IsNullOrWhiteSpace(clientCase.CourtUrl))
        {
            try
            {
                var courtData = await caseReader.ReadCaseAsync(clientCase.CourtUrl, ct, clientCase.CourtCaseNumber);
                var sourceTimeZone = await ResolveCourtSyncSourceTimeZoneAsync(clientCase, ct);
                var syncResult = await ApplySudrfCourtSyncAsync(clientCase, courtData, sourceTimeZone, actorName, checkedAt, ct);
                var syncMessage = !syncResult.HasChanges
                    ? $"Судебный источник проверен: новых изменений не найдено. Суд: {FormatCaseTextValue(clientCase.CourtName)}; № в суде: {FormatCaseTextValue(clientCase.CourtCaseNumber)}."
                    : $"Судебный источник обновлен: {FormatCourtSyncResult(syncResult)}";

                AppendCaseEvent(clientCase, $"{syncMessage} Инициатор: {actorName}.");
                _db.SetAuditReason($"Обновление дела #{clientCase.Id} из судебного источника {courtSyncProvider.Label}.");
                await _db.SaveChangesAsync(ct);
                if (syncResult.RequiresDeadlineTaskSync)
                {
                    await ClientCaseDeadlineTaskSyncService.SyncAsync(_db, clientCase.Id, User.GetUserId(), actorName, ct);
                }

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
                    CreatedCourtParties = syncResult.CreatedCourtParties,
                    CreatedCourtActDocuments = syncResult.CreatedCourtActDocuments,
                    CreatedProceduralDeadlines = syncResult.CreatedProceduralDeadlines,
                    NewItems = syncResult.NewItems,
                    ChangedItems = syncResult.ChangedItems,
                    RemovedItems = syncResult.RemovedItems,
                    ConflictItems = syncResult.ConflictItems,
                    NextHearingAt = clientCase.NextHearingAt,
                });
            }
            catch (SudrfCourtSyncException ex)
            {
                var errorMessage = $"Проверка судебного источника не выполнена: {ex.Message} Проверьте ссылку на карточку дела суда и повторите попытку.";
                AppendCaseEvent(clientCase, $"{errorMessage} Инициатор: {actorName}.");
                await NotifyCourtSyncFailedForCaseAsync(
                    clientCase,
                    courtSyncProvider.Label,
                    errorMessage,
                    User.GetUserId(),
                    actorName,
                    ct);
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
        var message = manualOnly
            ? $"Ручная проверка судебного источника зафиксирована без автоматического обновления. Суд: {FormatCaseTextValue(clientCase.CourtName)}; № в суде: {FormatCaseTextValue(clientCase.CourtCaseNumber)}.{manualCommentSuffix}"
            : string.IsNullOrWhiteSpace(clientCase.CourtUrl)
                ? $"Ручная проверка судебного источника зафиксирована: для автоматического обновления добавьте ссылку на карточку дела суда. Суд: {FormatCaseTextValue(clientCase.CourtName)}; № в суде: {FormatCaseTextValue(clientCase.CourtCaseNumber)}.{manualCommentSuffix}"
                : $"Ручная проверка судебного источника зафиксирована: выбранный источник пока не поддерживает автоматическое чтение карточки. Суд: {FormatCaseTextValue(clientCase.CourtName)}; № в суде: {FormatCaseTextValue(clientCase.CourtCaseNumber)}.{manualCommentSuffix}";

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

    private async Task<TimeZoneInfo?> ResolveCourtSyncSourceTimeZoneAsync(ClientCase clientCase, CancellationToken ct)
    {
        var userIds = new List<int>();
        if (User.GetUserId() is { } actorUserId)
        {
            userIds.Add(actorUserId);
        }

        if (clientCase.ResponsibleUserId is { } responsibleUserId && !userIds.Contains(responsibleUserId))
        {
            userIds.Add(responsibleUserId);
        }

        if (userIds.Count == 0)
        {
            return null;
        }

        var settings = await _db.UserSettings
            .AsNoTracking()
            .Where(item => userIds.Contains(item.UserId) && item.Key == CalendarTimeZoneHelper.UserTimeZoneSettingKey)
            .Select(item => new { item.UserId, item.Value })
            .ToListAsync(ct);

        foreach (var userId in userIds)
        {
            var timeZoneId = settings
                .FirstOrDefault(item => item.UserId == userId)?.Value;
            if (!string.IsNullOrWhiteSpace(timeZoneId))
            {
                return CalendarTimeZoneHelper.ResolveTimeZone(timeZoneId);
            }
        }

        return null;
    }

    private static DateTime NormalizeCourtSyncSourceDateTime(DateTime value, TimeZoneInfo? sourceTimeZone, bool dateOnly)
    {
        if (dateOnly)
        {
            return DateTime.SpecifyKind(value.Date, DateTimeKind.Utc);
        }

        if (sourceTimeZone is null)
        {
            return CalendarTimeZoneHelper.NormalizeUtcInstant(value);
        }

        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => CalendarTimeZoneHelper.FromUserLocalToUtc(value, sourceTimeZone),
        };
    }

    private async Task<CourtSyncApplyResult> ApplySudrfCourtSyncAsync(
        ClientCase clientCase,
        SudrfCourtCaseData courtData,
        TimeZoneInfo? sourceTimeZone,
        string actorName,
        DateTime checkedAt,
        CancellationToken ct)
    {
        var actorUserId = User.GetUserId();
        var changedFields = new List<string>();
        var createdEvents = new List<ClientCaseCourtEvent>();
        var updatedEvents = new List<ClientCaseCourtEvent>();
        var updatedCourtEvents = 0;
        var createdCourtParties = 0;
        var createdCourtActDocuments = 0;
        var createdProceduralDeadlines = 0;
        var newItems = new List<string>();
        var changedItems = new List<string>();
        var removedItems = new List<string>();
        var conflictItems = new List<string>();
        var requiresDeadlineTaskSync = false;
        var restoredDeadlineIds = new HashSet<int>();

        UpdateString("суд", clientCase.CourtName, courtData.CourtName, value => clientCase.CourtName = value, 200);
        UpdateString("номер дела в суде", clientCase.CourtCaseNumber, courtData.CaseNumber, value => clientCase.CourtCaseNumber = value, 100);
        UpdateString("ссылка на источник", clientCase.CourtUrl, courtData.SourceUrl, value => clientCase.CourtUrl = value, 1000);
        UpdateString("судья", clientCase.JudgeName, courtData.JudgeName, value => clientCase.JudgeName = value, 200);
        UpdateString("зал/место заседания", clientCase.Courtroom, courtData.NextHearingPlace, value => clientCase.Courtroom = value, 120);
        UpdateString("истец/заявитель", clientCase.Claimant, JoinParticipants(courtData.Participants, "ИСТЕЦ", "ЗАЯВИТЕЛ"), value => clientCase.Claimant = value, 300);
        UpdateString("ответчик", clientCase.Defendant, JoinParticipants(courtData.Participants, "ОТВЕТЧИК", "ДОЛЖНИК"), value => clientCase.Defendant = value, 300);
        UpdateString("третьи лица", clientCase.ThirdParties, JoinParticipants(courtData.Participants, "ТРЕТ", "ЗАИНТЕРЕС"), value => clientCase.ThirdParties = value, 1000);
        UpdateString("представители", clientCase.Representatives, JoinParticipants(courtData.Participants, "ПРЕДСТАВИТЕЛ"), value => clientCase.Representatives = value, 1000);

        var nextHearingAt = courtData.NextHearingAt.HasValue
            ? NormalizeCourtSyncSourceDateTime(courtData.NextHearingAt.Value, sourceTimeZone, courtData.NextHearingAt.Value.TimeOfDay == TimeSpan.Zero)
            : (DateTime?)null;
        if (nextHearingAt.HasValue && clientCase.NextHearingAt != nextHearingAt)
        {
            (clientCase.NextHearingAt.HasValue ? changedItems : newItems).Add("ближайшее заседание");
            clientCase.NextHearingAt = nextHearingAt;
            changedFields.Add("ближайшее заседание");
        }

        var hearings = courtData.MovementEvents
            .Where(static item => item.ScheduledAt.HasValue && item.Title.Contains("судебное заседание", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static item => item.ScheduledAt)
            .ToList();
        var existingEvents = await _db.ClientCaseCourtEvents
            .Where(item => item.ClientCaseId == clientCase.Id)
            .ToListAsync(ct);
        var existingEventsByExternalKey = existingEvents
            .Where(static item => !string.IsNullOrWhiteSpace(item.ExternalKey))
            .GroupBy(static item => item.ExternalKey!, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        var currentEventExternalKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var hearing in hearings)
        {
            var sourceScheduledAt = hearing.ScheduledAt!.Value;
            var allDay = sourceScheduledAt.TimeOfDay == TimeSpan.Zero;
            var scheduledAt = NormalizeCourtSyncSourceDateTime(sourceScheduledAt, sourceTimeZone, allDay);
            var title = Truncate(NormalizeOptional(hearing.Title) ?? "Судебное заседание", 200);
            var externalKey = BuildSudrfCourtEventExternalKey(courtData, sourceScheduledAt, title);
            currentEventExternalKeys.Add(externalKey);
            var existing = existingEventsByExternalKey.GetValueOrDefault(externalKey)
                ?? existingEvents.FirstOrDefault(item =>
                    CourtEventMatchesSudrfHearing(item, scheduledAt, title, courtData, hearing));
            var summary = Truncate(hearing.SourceSummary, 1000);

            if (existing is null)
            {
                var courtEvent = new ClientCaseCourtEvent
                {
                    ClientCaseId = clientCase.Id,
                    EventType = CourtSyncNormalization.EventTypes.Hearing,
                    Title = title,
                    Description = Truncate(hearing.SourceSummary, 1000),
                    ScheduledAt = scheduledAt,
                    AllDay = allDay,
                    CourtInstance = courtData.Instance,
                    CourtName = Truncate(NormalizeOptional(courtData.CourtName) ?? clientCase.CourtName, 200),
                    CourtCaseNumber = Truncate(NormalizeOptional(courtData.CaseNumber) ?? clientCase.CourtCaseNumber, 100),
                    Courtroom = Truncate(hearing.Place, 120),
                    JudgeName = Truncate(NormalizeOptional(courtData.JudgeName) ?? clientCase.JudgeName, 200),
                    SecretaryName = Truncate(NormalizeOptional(hearing.SecretaryName) ?? courtData.SecretaryName, 200),
                    MovementStatus = CourtSyncNormalization.ResolveMovementStatusFromResult(hearing.Result),
                    ResultSummary = Truncate(hearing.Result, 1000),
                    SourceType = "sudrf",
                    SourceUrl = Truncate(courtData.SourceUrl, 1000),
                    SourceCheckedAt = checkedAt,
                    SourceConfidence = "official-source",
                    ExternalKey = externalKey,
                    CreatedAt = checkedAt,
                    CreatedByUserId = actorUserId,
                    CreatedByUserName = Truncate(actorName, 256),
                    UpdatedAt = checkedAt,
                    UpdatedByUserId = actorUserId,
                    UpdatedByUserName = Truncate(actorName, 256),
                };
                _db.ClientCaseCourtEvents.Add(courtEvent);
                existingEvents.Add(courtEvent);
                existingEventsByExternalKey[externalKey] = courtEvent;
                createdEvents.Add(courtEvent);
                newItems.Add($"судебное событие «{title}»");
                continue;
            }

            if (!string.Equals(existing.SourceType, "sudrf", StringComparison.OrdinalIgnoreCase))
            {
                conflictItems.Add($"ручное событие «{existing.Title}» совпало с событием источника «{title}»");
                continue;
            }

            var updated = false;
            if (string.IsNullOrWhiteSpace(existing.ExternalKey))
            {
                existing.ExternalKey = externalKey;
                existingEventsByExternalKey[externalKey] = existing;
            }
            existing.SourceUrl = Truncate(courtData.SourceUrl, 1000);
            existing.SourceCheckedAt = checkedAt;
            existing.SourceConfidence = "official-source";
            updated |= UpdateCourtEventString(existing.CourtName, courtData.CourtName, value => existing.CourtName = value, 200);
            updated |= UpdateCourtEventString(existing.CourtCaseNumber, courtData.CaseNumber, value => existing.CourtCaseNumber = value, 100);
            updated |= UpdateCourtEventString(existing.Courtroom, hearing.Place, value => existing.Courtroom = value, 120);
            updated |= UpdateCourtEventString(existing.JudgeName, courtData.JudgeName, value => existing.JudgeName = value, 200);
            updated |= UpdateCourtEventString(existing.SecretaryName, FirstNonEmpty(hearing.SecretaryName, courtData.SecretaryName), value => existing.SecretaryName = value, 200);
            updated |= UpdateCourtEventString(existing.Description, hearing.SourceSummary, value => existing.Description = value, 1000);
            updated |= UpdateCourtEventString(existing.ResultSummary, hearing.Result, value => existing.ResultSummary = value, 1000);
            var movementStatus = CourtSyncNormalization.ResolveMovementStatusFromResult(hearing.Result);
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
                updatedEvents.Add(existing);
                updatedCourtEvents++;
                changedItems.Add($"судебное событие «{existing.Title}»");
            }
        }

        foreach (var missingEvent in existingEvents.Where(item =>
                     string.Equals(item.SourceType, "sudrf", StringComparison.OrdinalIgnoreCase)
                     && IsCurrentCourtSyncSource(item.SourceUrl)
                     && !string.IsNullOrWhiteSpace(item.ExternalKey)
                     && !string.Equals(item.MovementStatus, "archived", StringComparison.OrdinalIgnoreCase)
                     && !currentEventExternalKeys.Contains(item.ExternalKey)))
        {
            removedItems.Add($"судебное событие «{missingEvent.Title}»");
            missingEvent.MovementStatus = "archived";
            missingEvent.UpdatedAt = checkedAt;
            missingEvent.UpdatedByUserId = actorUserId;
            missingEvent.UpdatedByUserName = Truncate(actorName, 256);
            AddCourtEventHistory(
                missingEvent,
                "archived",
                "Событие перемещено в архив: оно исчезло из судебного источника.",
                actorUserId,
                actorName);
        }

        if (createdEvents.Count > 0)
        {
            await _db.SaveChangesAsync(ct);
            foreach (var courtEvent in createdEvents)
            {
                AddCourtEventHistory(courtEvent, "created", $"Событие создано из ГАС «Правосудие»: {courtEvent.Title}.", actorUserId, actorName);
            }
        }

        foreach (var courtEvent in createdEvents)
        {
            await AddCourtEventUserNotificationsAsync(clientCase, courtEvent, "created", actorUserId, actorName, ct);
        }

        foreach (var courtEvent in updatedEvents)
        {
            await AddCourtEventUserNotificationsAsync(clientCase, courtEvent, "updated", actorUserId, actorName, ct);
        }

        var partyCandidates = BuildSudrfCourtPartyCandidates(courtData.Participants);
        var existingParties = await _db.ClientCaseCourtParties
            .Where(item => item.ClientCaseId == clientCase.Id)
            .ToListAsync(ct);
        var currentPartyIdentities = partyCandidates
            .Select(static candidate => BuildCourtPartyIdentity(candidate.DisplayName, candidate.Side, candidate.ProcessRole))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in partyCandidates)
        {
            var existing = existingParties.FirstOrDefault(item =>
                string.Equals(NormalizeCourtPartyName(item.DisplayName), NormalizeCourtPartyName(candidate.DisplayName), StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Side, candidate.Side, StringComparison.OrdinalIgnoreCase)
                && string.Equals(NormalizeCourtPartyName(item.ProcessRole), NormalizeCourtPartyName(candidate.ProcessRole), StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                if (!string.Equals(existing.SourceType, "sudrf", StringComparison.OrdinalIgnoreCase))
                {
                    conflictItems.Add($"ручной участник «{existing.DisplayName}» совпал с участником источника");
                    continue;
                }

                existing.SourceUrl = Truncate(courtData.SourceUrl, 1000);
                existing.SourceCheckedAt = checkedAt;
                existing.SourceConfidence = "official-source";
                if (existing.IsArchived)
                {
                    existing.IsArchived = false;
                    existing.UpdatedAt = checkedAt;
                    existing.UpdatedByUserId = actorUserId;
                    existing.UpdatedByUserName = Truncate(actorName, 256);
                    changedItems.Add($"участник процесса «{existing.DisplayName}»");
                }

                continue;
            }

            var courtParty = new ClientCaseCourtParty
            {
                ClientCaseId = clientCase.Id,
                DisplayName = candidate.DisplayName,
                Side = candidate.Side,
                ProcessRole = candidate.ProcessRole,
                RepresentativeName = candidate.RepresentativeName,
                Notes = candidate.Notes,
                SourceType = "sudrf",
                SourceUrl = Truncate(courtData.SourceUrl, 1000),
                SourceCheckedAt = checkedAt,
                SourceConfidence = "official-source",
                CreatedAt = checkedAt,
                CreatedByUserId = actorUserId,
                CreatedByUserName = Truncate(actorName, 256),
                UpdatedAt = checkedAt,
                UpdatedByUserId = actorUserId,
                UpdatedByUserName = Truncate(actorName, 256),
            };
            _db.ClientCaseCourtParties.Add(courtParty);
            existingParties.Add(courtParty);
            createdCourtParties++;
            newItems.Add($"участник процесса «{courtParty.DisplayName}»");
        }

        foreach (var missingParty in existingParties.Where(item =>
                     string.Equals(item.SourceType, "sudrf", StringComparison.OrdinalIgnoreCase)
                     && IsCurrentCourtSyncSource(item.SourceUrl)
                     && !item.IsArchived
                     && !currentPartyIdentities.Contains(BuildCourtPartyIdentity(item.DisplayName, item.Side, item.ProcessRole))))
        {
            removedItems.Add($"участник процесса «{missingParty.DisplayName}»");
            missingParty.IsArchived = true;
            missingParty.UpdatedAt = checkedAt;
            missingParty.UpdatedByUserId = actorUserId;
            missingParty.UpdatedByUserName = Truncate(actorName, 256);
        }

        if (createdCourtParties > 0)
        {
            changedFields.Add("участники процесса");
        }

        var courtActsWithKeys = courtData.CourtActs
            .Select(courtAct => new
            {
                CourtAct = courtAct,
                DocumentKey = BuildSudrfCourtActDocumentKey(courtAct),
                LegacyDocumentKey = BuildLegacySudrfCourtActDocumentKey(courtAct),
                FileName = BuildSudrfCourtActFileName(courtAct),
            })
            .ToList();
        var courtActDocumentKeys = courtActsWithKeys
            .SelectMany(static item => new[] { item.DocumentKey, item.LegacyDocumentKey })
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var currentCourtActDocumentKeys = courtActDocumentKeys.ToHashSet(StringComparer.Ordinal);
        var existingCourtActDocuments = await _db.ClientCaseDocuments
            .Where(item =>
                item.ClientCaseId == clientCase.Id
                && (item.SourceType == "sudrf"
                    || courtActDocumentKeys.Contains(item.DocumentKey)
                    || item.DocumentType == CourtSyncNormalization.DocumentTypes.CourtAct))
            .OrderByDescending(item => item.Version)
            .ToListAsync(ct);
        var existingCourtActDocumentsByKey = existingCourtActDocuments
            .GroupBy(static item => item.DocumentKey, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        var existingCourtActDocumentsByFileName = existingCourtActDocuments
            .Where(static item => item.DocumentType == CourtSyncNormalization.DocumentTypes.CourtAct)
            .GroupBy(static item => item.OriginalFileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);
        var existingCourtActDeadlines = await _db.ClientCaseProceduralDeadlines
            .Where(item => item.ClientCaseId == clientCase.Id && item.SourceType == "sudrf")
            .ToListAsync(ct);
        var currentDeadlineIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var courtActWithKey in courtActsWithKeys)
        {
            var courtAct = courtActWithKey.CourtAct;
            var documentKey = courtActWithKey.DocumentKey;
            var documentByKnownKey = existingCourtActDocumentsByKey.GetValueOrDefault(documentKey)
                ?? existingCourtActDocumentsByKey.GetValueOrDefault(courtActWithKey.LegacyDocumentKey);
            var document = documentByKnownKey
                ?? existingCourtActDocumentsByFileName.GetValueOrDefault(courtActWithKey.FileName);
            if (document is not null
                && documentByKnownKey is null
                && !string.Equals(document.SourceType, "sudrf", StringComparison.OrdinalIgnoreCase))
            {
                conflictItems.Add($"ручной документ «{document.OriginalFileName}» совпал с судебным актом «{courtAct.Title}»");
                continue;
            }

            if (document is null)
            {
                document = await SaveGeneratedCaseDocumentAsync(
                    clientCase,
                    documentKey,
                    CourtSyncNormalization.DocumentTypes.CourtAct,
                    "received",
                    courtActWithKey.FileName,
                    BuildSudrfCourtActHtml(clientCase, courtData, courtAct),
                    "Created",
                    $"Судебный акт «{courtAct.Title}» добавлен из ГАС «Правосудие».",
                    $"Судебный акт «{courtAct.Title}» добавлен из ГАС «Правосудие».",
                    ct);
                createdCourtActDocuments++;
                _courtSyncMetrics?.RecordDocument(document.SizeBytes);
                newItems.Add($"судебный акт «{courtAct.Title}»");
            }
            else if (!string.Equals(document.DocumentKey, documentKey, StringComparison.Ordinal))
            {
                document.DocumentKey = documentKey;
                changedItems.Add($"судебный акт «{courtAct.Title}»");
            }

            if (string.Equals(document.Status, "archived", StringComparison.OrdinalIgnoreCase))
            {
                document.Status = "received";
                changedItems.Add($"судебный акт «{courtAct.Title}» восстановлен из архива");
            }

            document.SourceType = "sudrf";
            document.SourceUrl = Truncate(FirstNonEmpty(courtAct.SourceUrl, courtData.SourceUrl), 1000);
            document.SourceCheckedAt = checkedAt;
            document.SourceConfidence = "official-source";
            existingCourtActDocumentsByKey[documentKey] = document;
            existingCourtActDocumentsByFileName[courtActWithKey.FileName] = document;

            var createdDeadlinesForAct = await SyncSudrfCourtActDeadlinesAsync(
                clientCase,
                courtData,
                courtAct,
                document,
                actorUserId,
                actorName,
                checkedAt,
                existingCourtActDeadlines,
                currentDeadlineIdentities,
                changedItems,
                restoredDeadlineIds,
                ct);
            createdProceduralDeadlines += createdDeadlinesForAct;
            if (createdDeadlinesForAct > 0)
            {
                requiresDeadlineTaskSync = true;
                newItems.Add($"процессуальные сроки по акту «{courtAct.Title}» ({createdDeadlinesForAct})");
            }
        }

        var removedCourtActDocumentIds = existingCourtActDocuments
            .Where(item =>
                string.Equals(item.SourceType, "sudrf", StringComparison.OrdinalIgnoreCase)
                && IsCurrentCourtSyncSource(item.SourceUrl)
                && !string.Equals(item.Status, "archived", StringComparison.OrdinalIgnoreCase)
                && !currentCourtActDocumentKeys.Contains(item.DocumentKey))
            .Select(static item => item.Id)
            .ToHashSet();
        foreach (var missingDocument in existingCourtActDocuments.Where(item => removedCourtActDocumentIds.Contains(item.Id)))
        {
            removedItems.Add($"судебный акт «{missingDocument.OriginalFileName}»");
            missingDocument.Status = "archived";
        }

        foreach (var missingDeadline in existingCourtActDeadlines.Where(item =>
                     IsCurrentCourtSyncSource(item.SourceUrl)
                     && !string.Equals(item.Status, "archived", StringComparison.OrdinalIgnoreCase)
                     && item.ClientCaseDocumentId.HasValue
                     && !currentDeadlineIdentities.Contains(BuildCourtDeadlineIdentity(
                         item.ClientCaseDocumentId.Value,
                         item.DeadlineType))))
        {
            removedItems.Add($"процессуальный срок «{missingDeadline.Title}»");
            missingDeadline.Status = "archived";
            missingDeadline.UpdatedAt = checkedAt;
            missingDeadline.UpdatedByUserId = actorUserId;
            missingDeadline.UpdatedByUserName = Truncate(actorName, 256);
            requiresDeadlineTaskSync = true;
        }
        requiresDeadlineTaskSync |= restoredDeadlineIds.Count > 0;

        if (createdCourtActDocuments > 0)
        {
            changedFields.Add("судебные акты");
        }

        if (createdProceduralDeadlines > 0)
        {
            changedFields.Add("процессуальные сроки");
        }

        return new CourtSyncApplyResult(
            changedFields,
            createdEvents.Count,
            updatedCourtEvents,
            createdCourtParties,
            createdCourtActDocuments,
            createdProceduralDeadlines,
            newItems.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            changedItems.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            removedItems.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            conflictItems.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            requiresDeadlineTaskSync);

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
            (string.IsNullOrWhiteSpace(currentValue) ? newItems : changedItems).Add(label);
        }

        static string BuildCourtPartyIdentity(string displayName, string side, string processRole)
            => string.Join(
                "|",
                NormalizeCourtPartyName(displayName),
                NormalizeOptional(side)?.ToLowerInvariant() ?? string.Empty,
                NormalizeCourtPartyName(processRole));

        bool IsCurrentCourtSyncSource(string? sourceUrl)
            => string.Equals(
                NormalizeOptional(sourceUrl),
                NormalizeOptional(courtData.SourceUrl),
                StringComparison.OrdinalIgnoreCase);

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

    private static bool CourtEventMatchesSudrfHearing(
        ClientCaseCourtEvent existing,
        DateTime scheduledAt,
        string title,
        SudrfCourtCaseData courtData,
        SudrfCourtMovementEvent hearing)
    {
        if (existing.ScheduledAt != scheduledAt)
        {
            return false;
        }

        var existingTitle = NormalizeCourtSyncText(existing.Title);
        var sourceTitle = NormalizeCourtSyncText(title);
        if (string.Equals(existingTitle, sourceTitle, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var isExistingHearing =
            string.Equals(existing.EventType, "hearing", StringComparison.OrdinalIgnoreCase)
            || existingTitle.Contains("судебное заседание", StringComparison.OrdinalIgnoreCase);
        if (!isExistingHearing)
        {
            return false;
        }

        if (string.Equals(existing.SourceType, "sudrf", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return SameNormalizedCourtSyncText(existing.CourtCaseNumber, courtData.CaseNumber)
            || SameNormalizedCourtSyncText(existing.Courtroom, hearing.Place)
            || SameNormalizedCourtSyncText(existing.CourtName, courtData.CourtName);
    }

    private async Task<int> SyncSudrfCourtActDeadlinesAsync(
        ClientCase clientCase,
        SudrfCourtCaseData courtData,
        SudrfCourtAct courtAct,
        ClientCaseDocument document,
        int? actorUserId,
        string actorName,
        DateTime checkedAt,
        IList<ClientCaseProceduralDeadline> existingDeadlines,
        ISet<string> currentDeadlineIdentities,
        ICollection<string> changedItems,
        ISet<int> restoredDeadlineIds,
        CancellationToken ct)
    {
        if (!courtAct.ActDate.HasValue)
        {
            return 0;
        }

        var deadlineSpecs = BuildSudrfCourtActDeadlineSpecs(courtData, courtAct).ToList();
        if (deadlineSpecs.Count == 0)
        {
            return 0;
        }

        foreach (var spec in deadlineSpecs)
        {
            currentDeadlineIdentities.Add(BuildCourtDeadlineIdentity(document.Id, spec.DeadlineType));
        }

        var created = 0;
        var createdDeadlines = new List<ClientCaseProceduralDeadline>();
        foreach (var spec in deadlineSpecs)
        {
            var existing = existingDeadlines.FirstOrDefault(item =>
                item.ClientCaseDocumentId == document.Id
                && item.DeadlineType == spec.DeadlineType);
            if (existing is not null)
            {
                existing.SourceUrl = Truncate(FirstNonEmpty(courtAct.SourceUrl, courtData.SourceUrl), 1000);
                existing.SourceCheckedAt = checkedAt;
                existing.SourceConfidence = "official-source";
                if (string.Equals(existing.Status, "archived", StringComparison.OrdinalIgnoreCase))
                {
                    existing.Status = "active";
                    existing.UpdatedAt = checkedAt;
                    existing.UpdatedByUserId = actorUserId;
                    existing.UpdatedByUserName = Truncate(actorName, 256);
                    changedItems.Add($"процессуальный срок «{existing.Title}» восстановлен из архива");
                    restoredDeadlineIds.Add(existing.Id);
                }

                continue;
            }

            var deadline = new ClientCaseProceduralDeadline
            {
                ClientCaseId = clientCase.Id,
                DeadlineType = spec.DeadlineType,
                Title = spec.Title,
                Description = spec.Description,
                DueAt = spec.DueAt,
                AllDay = true,
                Status = "active",
                WarningDaysBefore = 7,
                ClientCaseDocumentId = document.Id,
                SourceType = "sudrf",
                SourceUrl = Truncate(FirstNonEmpty(courtAct.SourceUrl, courtData.SourceUrl), 1000),
                SourceCheckedAt = checkedAt,
                SourceConfidence = "official-source",
                Notes = $"Создано автоматически из ГАС «Правосудие». Документ: {courtAct.Title}.",
                CreatedAt = checkedAt,
                CreatedByUserId = actorUserId,
                CreatedByUserName = Truncate(actorName, 256),
                UpdatedAt = checkedAt,
                UpdatedByUserId = actorUserId,
                UpdatedByUserName = Truncate(actorName, 256),
            };
            _db.ClientCaseProceduralDeadlines.Add(deadline);
            existingDeadlines.Add(deadline);
            createdDeadlines.Add(deadline);
            created++;

            if (spec.DeadlineType == "appeal")
            {
                clientCase.CourtDecisionAt ??= courtAct.ActDate.Value.Date;
                clientCase.AppealDeadlineAt ??= spec.DueAt.Date;
            }
            else if (spec.DeadlineType == "cassation")
            {
                clientCase.CassationDeadlineAt ??= spec.DueAt.Date;
            }
        }

        if (created > 0)
        {
            AppendCaseEvent(clientCase, $"Процессуальные сроки по судебному акту «{courtAct.Title}» добавлены из ГАС «Правосудие». Добавлено: {created}.");
            await _db.SaveChangesAsync(ct);

            foreach (var deadline in createdDeadlines)
            {
                await AddProceduralDeadlineUserNotificationsAsync(clientCase, deadline, "created", actorUserId, actorName, ct);
            }
        }

        return created;
    }

    private static string BuildCourtDeadlineIdentity(int documentId, string deadlineType)
        => $"{documentId}:{NormalizeOptional(deadlineType)?.ToLowerInvariant()}";

    private static IEnumerable<SudrfCourtActDeadlineSpec> BuildSudrfCourtActDeadlineSpecs(
        SudrfCourtCaseData courtData,
        SudrfCourtAct courtAct)
    {
        if (!courtAct.ActDate.HasValue)
        {
            yield break;
        }

        var title = courtAct.Title;
        var result = $"{courtAct.Title} {courtAct.Result}";
        if (courtData.Instance.Equals("appeal", StringComparison.OrdinalIgnoreCase)
            || result.Contains("апелляцион", StringComparison.OrdinalIgnoreCase))
        {
            var dueAt = ProceduralDeadlineCalculator.CalculateByMonths(courtAct.ActDate.Value, 3);
            yield return new SudrfCourtActDeadlineSpec(
                "cassation",
                "Срок кассационного обжалования",
                $"Срок рассчитан от даты судебного акта «{title}» из ГАС «Правосудие».",
                dueAt);
            yield break;
        }

        if (result.Contains("решение", StringComparison.OrdinalIgnoreCase))
        {
            var dueAt = ProceduralDeadlineCalculator.CalculateByMonths(courtAct.ActDate.Value, 1);
            yield return new SudrfCourtActDeadlineSpec(
                "appeal",
                "Срок апелляционного обжалования",
                $"Срок рассчитан от даты судебного акта «{title}» из ГАС «Правосудие».",
                dueAt);
        }
    }

    private static string BuildSudrfCourtActDocumentKey(SudrfCourtAct courtAct)
    {
        var hash = BuildStableCourtSyncHash(
            courtAct.SourceUrl,
            NormalizeCourtSyncText(courtAct.Title),
            courtAct.ActDate?.ToString("O", CultureInfo.InvariantCulture));
        return $"sudrf-act-{hash}";
    }

    private static string BuildLegacySudrfCourtActDocumentKey(SudrfCourtAct courtAct)
    {
        var raw = $"{courtAct.Title}|{courtAct.ActDate:yyyy-MM-dd}|{courtAct.Result}|{courtAct.SourceUrl}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        return $"sudrf-act-{hash[..24]}";
    }

    private static string BuildSudrfCourtEventExternalKey(
        SudrfCourtCaseData courtData,
        DateTime sourceScheduledAt,
        string title)
        => $"sudrf-event-{BuildStableCourtSyncHash(
            courtData.SourceUrl,
            NormalizeCourtSyncText(courtData.CaseNumber),
            NormalizeCourtSyncText(courtData.Instance),
            sourceScheduledAt.ToString("O", CultureInfo.InvariantCulture),
            NormalizeCourtSyncText(title))}";

    private static string BuildStableCourtSyncHash(params string?[] parts)
    {
        var raw = string.Join(
            '\u001f',
            parts.Select(static part => NormalizeOptional(part)?.Trim() ?? string.Empty));
        return Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))
            .ToLowerInvariant()[..32];
    }

    private static string BuildSudrfCourtActFileName(SudrfCourtAct courtAct)
    {
        var date = courtAct.ActDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "без-даты";
        return $"{date} - {courtAct.Title}.html";
    }

    private static string BuildSudrfCourtActHtml(ClientCase clientCase, SudrfCourtCaseData courtData, SudrfCourtAct courtAct)
        => $"""
           <!doctype html>
           <html lang="ru">
           <head>
             <meta charset="utf-8">
             <title>{Html(courtAct.Title)}</title>
           </head>
           <body>
             <h1>{Html(courtAct.Title)}</h1>
             <p><strong>Дело CRM:</strong> {Html(clientCase.Title)}</p>
             <p><strong>Суд:</strong> {Html(courtData.CourtName)}</p>
             <p><strong>Номер дела в суде:</strong> {Html(courtData.CaseNumber)}</p>
             <p><strong>Дата судебного акта:</strong> {Html(FormatTemplateDate(courtAct.ActDate))}</p>
             <p><strong>Дата размещения:</strong> {Html(FormatTemplateDate(courtAct.PostedAt))}</p>
             <p><strong>Результат:</strong> {Html(courtAct.Result)}</p>
             <p><strong>Источник:</strong> <a href="{Html(courtAct.SourceUrl)}">{Html(courtAct.SourceUrl)}</a></p>
             <hr>
             <p>{Html(courtAct.Summary)}</p>
           </body>
           </html>
           """;

    private static string FormatCourtSyncResult(CourtSyncApplyResult result)
    {
        var parts = new List<string>();
        if (result.NewItems.Count > 0)
        {
            parts.Add($"новые данные: {result.NewItems.Count}");
        }

        if (result.ChangedItems.Count > 0)
        {
            parts.Add($"измененные данные: {result.ChangedItems.Count}");
        }

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

        if (result.CreatedCourtParties > 0)
        {
            parts.Add($"добавлено участников процесса: {result.CreatedCourtParties}");
        }

        if (result.CreatedCourtActDocuments > 0)
        {
            parts.Add($"добавлено судебных актов: {result.CreatedCourtActDocuments}");
        }

        if (result.CreatedProceduralDeadlines > 0)
        {
            parts.Add($"добавлено процессуальных сроков: {result.CreatedProceduralDeadlines}");
        }

        if (result.RemovedItems.Count > 0)
        {
            parts.Add($"исчезло из источника: {result.RemovedItems.Count}");
        }

        if (result.ConflictItems.Count > 0)
        {
            parts.Add($"требует проверки конфликтов: {result.ConflictItems.Count}");
        }

        return string.Join("; ", parts) + ".";
    }

    private static IReadOnlyList<SudrfCourtPartyCandidate> BuildSudrfCourtPartyCandidates(
        IReadOnlyList<SudrfCourtParticipant> participants)
    {
        var candidates = new List<SudrfCourtPartyCandidate>();
        foreach (var participant in participants)
        {
            var name = NormalizeOptional(participant.Name);
            if (name is null)
            {
                continue;
            }

            var side = CourtSyncNormalization.ResolvePartySideFromRole(participant.Role);
            var processRole = Truncate(NormalizeOptional(participant.Role) ?? "Участник процесса", 120);
            var notesParts = new List<string> { "Добавлено из ГАС «Правосудие»." };
            if (!string.IsNullOrWhiteSpace(participant.Role))
            {
                notesParts.Add($"Роль на сайте суда: {participant.Role}.");
            }

            if (!string.IsNullOrWhiteSpace(participant.Details))
            {
                notesParts.Add(participant.Details);
            }

            candidates.Add(new SudrfCourtPartyCandidate(
                Truncate(name, 300),
                side,
                processRole,
                string.Empty,
                Truncate(string.Join(' ', notesParts), 1000)));
        }

        return candidates
            .GroupBy(static item => new
            {
                Name = NormalizeCourtPartyName(item.DisplayName),
                item.Side,
                Role = NormalizeCourtPartyName(item.ProcessRole),
            })
            .Select(static group => group.First())
            .ToList();
    }

    private static string NormalizeCourtPartyName(string value)
        => NormalizeHistoryText(value).ToUpperInvariant();

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
        if (!TryNormalizeInactivityReminderState(
                model.InactivityRemindersEnabled,
                model.InactivityRemindersDisabledReason,
                existingReason: null,
                requireNewReason: !model.InactivityRemindersEnabled,
                out var inactivityDisabledReason,
                out var inactivityValidationError))
        {
            return BadRequest(inactivityValidationError);
        }
        model.InactivityRemindersDisabledReason = inactivityDisabledReason;
        ApplyCaseDetails(model, model);
        if (!await CanViewCaseFinanceAsync(ct))
        {
            model.FeeAmount = 0m;
        }
        ApplyCaseControlState(model, model.IsUnderControl, User.GetUserId(), User.GetDisplayName());
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
        var inactivityStateChanged = e.InactivityRemindersEnabled != model.InactivityRemindersEnabled;
        if (!TryNormalizeInactivityReminderState(
                model.InactivityRemindersEnabled,
                model.InactivityRemindersDisabledReason,
                e.InactivityRemindersDisabledReason,
                requireNewReason: inactivityStateChanged && !model.InactivityRemindersEnabled,
                out var inactivityDisabledReason,
                out var inactivityValidationError))
        {
            return BadRequest(inactivityValidationError);
        }
        model.InactivityRemindersDisabledReason = inactivityDisabledReason;

        var shouldCloseLinkedTasks = !IsFinishedCaseStatus(e.Status) && IsFinishedCaseStatus(model.Status);
        var previousFeeAmount = e.FeeAmount;
        var canViewFinance = await CanViewCaseFinanceAsync(ct);
        e.Title = model.Title;
        e.Description = model.Description;
        e.Status = model.Status;
        e.ClientId = model.ClientId;
        e.LegalEntityId = model.LegalEntityId;
        var previousHiddenFromExecutor = e.IsHiddenFromExecutor;
        ApplyCaseDetails(e, model);
        if (!canViewFinance)
        {
            e.FeeAmount = previousFeeAmount;
        }
        ApplyCaseControlState(e, model.IsUnderControl, User.GetUserId(), User.GetDisplayName());
        if (previousHiddenFromExecutor != e.IsHiddenFromExecutor)
        {
            ApplyHiddenFromExecutorMetadata(e, e.IsHiddenFromExecutor, User.GetUserId(), User.GetDisplayName());
        }
        if (inactivityStateChanged)
        {
            var inactivityAuditReason = e.InactivityRemindersEnabled
                ? "Автоконтроль дела включен."
                : $"Автоконтроль дела выключен. Причина: {e.InactivityRemindersDisabledReason}";
            AppendCaseEvent(e, $"{inactivityAuditReason} Изменил: {User.GetDisplayName()}.");
            _db.SetAuditReason(inactivityAuditReason);
        }

        if (shouldCloseLinkedTasks)
        {
            await ClientCaseTaskSyncService.CloseActiveTasksForClosedCaseAsync(_db, e.Id, User.GetUserId(), User.GetDisplayName(), ct);
        }

        await _db.SaveChangesAsync(ct);
        await ClientCaseDeadlineTaskSyncService.SyncAsync(_db, e.Id, User.GetUserId(), User.GetDisplayName(), ct);
        return Ok(e);
    }

    [HttpPatch("{id:int}/control")]
    public async Task<IActionResult> UpdateControl(int id, [FromBody] UpdateClientCaseControlRequest request, CancellationToken ct)
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

        if (!ApplyCaseControlState(clientCase, request.IsUnderControl, User.GetUserId(), User.GetDisplayName()))
        {
            return Ok(clientCase);
        }

        AppendCaseEvent(
            clientCase,
            request.IsUnderControl
                ? $"Дело поставлено на контроль. Автор: {User.GetDisplayName()}."
                : $"Дело снято с контроля. Автор: {User.GetDisplayName()}.");
        _db.SetAuditReason(request.IsUnderControl
            ? $"Дело поставлено на контроль: {clientCase.Title}"
            : $"Дело снято с контроля: {clientCase.Title}");
        await _db.SaveChangesAsync(ct);
        return Ok(clientCase);
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
            && !request.InactivityRemindersEnabled.HasValue
            && !request.IsUnderControl.HasValue)
        {
            return BadRequest("Укажите новый статус, ответственного, стадию, приоритет, режим доступа, автоконтроль или контроль.");
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

        var requiresInactivityDisableReason =
            request.InactivityRemindersEnabled == false &&
            cases.Any(item => item.InactivityRemindersEnabled);
        if (requiresInactivityDisableReason &&
            string.IsNullOrWhiteSpace(request.InactivityRemindersDisabledReason))
        {
            return BadRequest("Укажите причину выключения автоконтроля.");
        }
        var inactivityDisabledReason = request.InactivityRemindersEnabled == false
            ? Truncate(request.InactivityRemindersDisabledReason?.Trim() ?? string.Empty, 1000)
            : string.Empty;

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
                changes.Add(nextEnabled
                    ? "автоконтроль: включен"
                    : $"автоконтроль: выключен, причина «{inactivityDisabledReason}»");
                item.InactivityRemindersEnabled = nextEnabled;
                item.InactivityRemindersDisabledReason = nextEnabled ? string.Empty : inactivityDisabledReason;
            }

            if (request.IsUnderControl.HasValue && ApplyCaseControlState(item, request.IsUnderControl.Value, actorUserId, actorName))
            {
                changes.Add(request.IsUnderControl.Value ? "контроль: включен" : "контроль: выключен");
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
        target.InactivityRemindersDisabledReason = source.InactivityRemindersEnabled
            ? string.Empty
            : Truncate(source.InactivityRemindersDisabledReason?.Trim() ?? string.Empty, 1000);
        target.IsHiddenFromExecutor = source.IsHiddenFromExecutor;
    }

    private static bool TryNormalizeInactivityReminderState(
        bool enabled,
        string? requestedReason,
        string? existingReason,
        bool requireNewReason,
        out string normalizedReason,
        out string error)
    {
        normalizedReason = string.Empty;
        error = string.Empty;
        if (enabled)
        {
            return true;
        }

        var reason = requestedReason?.Trim();
        if (string.IsNullOrWhiteSpace(reason) && !requireNewReason)
        {
            reason = existingReason?.Trim();
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            error = "Укажите причину выключения автоконтроля.";
            return false;
        }

        normalizedReason = Truncate(reason, 1000);
        return true;
    }

    private static bool ApplyCaseControlState(
        ClientCase clientCase,
        bool isUnderControl,
        int? actorUserId,
        string actorName)
    {
        if (clientCase.IsUnderControl == isUnderControl
            && (!isUnderControl || clientCase.ControlMarkedAt.HasValue))
        {
            return false;
        }

        clientCase.IsUnderControl = isUnderControl;
        if (isUnderControl)
        {
            clientCase.ControlMarkedAt = DateTime.UtcNow;
            clientCase.ControlMarkedByUserId = actorUserId;
            clientCase.ControlMarkedByUserName = Truncate(actorName, 256);
        }
        else
        {
            clientCase.ControlMarkedAt = null;
            clientCase.ControlMarkedByUserId = null;
            clientCase.ControlMarkedByUserName = string.Empty;
        }

        return true;
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
            .Where(payment => payment.Status != PaymentStatus.Cancelled)
            .Where(payment =>
                (payment.ClientCaseId.HasValue && caseIds.Contains(payment.ClientCaseId.Value)) ||
                payment.InvoiceClientCases.Any(link => caseIds.Contains(link.ClientCaseId)))
            .Select(payment => new Payment
            {
                Id = payment.Id,
                Account = payment.Account,
                AccountDate = payment.AccountDate,
                InvoiceId = payment.InvoiceId,
                LegacyAccountNumber = payment.LegacyAccountNumber,
                LegacyAccountDate = payment.LegacyAccountDate,
                Amount = payment.Amount,
                ClientCaseId = payment.ClientCaseId,
                ClientId = payment.ClientId,
                ExpenseClientId = payment.ExpenseClientId,
                DocumentKind = payment.DocumentKind,
                Date = payment.Date,
                PaidAmount = payment.PaidAmount,
                SystemNotes = payment.SystemNotes,
                Status = payment.Status,
                Type = payment.Type,
            })
            .ToListAsync(ct);

        var paymentIds = payments.Select(payment => payment.Id).ToArray();
        if (paymentIds.Length > 0)
        {
            var links = await _db.InvoiceClientCases
                .AsNoTracking()
                .Where(link => paymentIds.Contains(link.PaymentId) && caseIds.Contains(link.ClientCaseId))
                .Select(link => new
                {
                    link.PaymentId,
                    link.ClientCaseId,
                })
                .ToListAsync(ct);
            var paymentById = payments.ToDictionary(payment => payment.Id);
            foreach (var link in links)
            {
                paymentById[link.PaymentId].InvoiceClientCases.Add(new InvoiceClientCase
                {
                    PaymentId = link.PaymentId,
                    ClientCaseId = link.ClientCaseId,
                });
            }
        }

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

    private static void HideCaseFinanceData(IEnumerable<ClientCase> cases)
    {
        foreach (var clientCase in cases)
        {
            clientCase.FeeAmount = 0m;
            clientCase.IncomeTotal = 0m;
            clientCase.IncomePaid = 0m;
            clientCase.IncomeOutstanding = 0m;
            clientCase.IncomeCount = 0;
            clientCase.ExpenseTotal = 0m;
            clientCase.ExpensePaid = 0m;
            clientCase.ExpenseOutstanding = 0m;
            clientCase.ExpenseCount = 0;
            clientCase.Payments = new List<Payment>();
            clientCase.InvoiceClientCases = new List<InvoiceClientCase>();
        }
    }

    private async Task ApplyTaskSummaryAsync(
        IReadOnlyCollection<ClientCase> cases,
        CancellationToken ct,
        bool includeInactivityControlSummary = false)
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
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(task => task.ClientCaseId.HasValue && caseIds.Contains(task.ClientCaseId.Value))
            .GroupBy(task => task.ClientCaseId!.Value)
            .Select(group => new
            {
                CaseId = group.Key,
                Total = group.Count(task => !EF.Property<bool>(task, "IsDeleted")),
                Open = group.Count(task =>
                    !EF.Property<bool>(task, "IsDeleted") &&
                    task.Status != "done" &&
                    task.Status != "cancelled"),
                Completed = group.Count(task =>
                    !EF.Property<bool>(task, "IsDeleted") &&
                    (task.Status == "done" || task.Status == "cancelled")),
                Overdue = group.Count(task =>
                    !EF.Property<bool>(task, "IsDeleted") &&
                    task.Status != "done" &&
                    task.Status != "cancelled" &&
                    task.DueAt < now),
                LastTaskAt = group.Max(task =>
                    !EF.Property<bool>(task, "IsDeleted")
                        ? (DateTime?)(task.UpdatedAt ?? task.CreatedAt)
                        : null),
                LastMeaningfulTaskAt = group.Max(task =>
                    !EF.Property<bool>(task, "IsDeleted") &&
                    !task.TagsJson.Contains(CaseInactivityAutomationTag)
                        ? (DateTime?)(task.UpdatedAt ?? task.CreatedAt)
                        : null),
                LastControlCycleEndedAt = group.Max(task =>
                        task.TagsJson.Contains(CaseInactivityAutomationTag) &&
                    (EF.Property<bool>(task, "IsDeleted") ||
                     task.Status == "done" ||
                     task.Status == "cancelled")
                        ? EF.Property<DateTime?>(task, "DeletedAt") ??
                          task.UpdatedAt ??
                          (DateTime?)task.CreatedAt
                        : null),
                ActiveOtherTaskCount = group.Count(task =>
                    !EF.Property<bool>(task, "IsDeleted") &&
                    ActiveTaskStatuses.Contains(task.Status) &&
                    !task.TagsJson.Contains(CaseInactivityAutomationTag)),
                ActiveControlNextCheckAt = group.Min(task =>
                    !EF.Property<bool>(task, "IsDeleted") &&
                    ActiveTaskStatuses.Contains(task.Status) &&
                    task.TagsJson.Contains(CaseInactivityAutomationTag)
                        ? task.InactivityControlNextCheckAt
                        : null),
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

        if (!includeInactivityControlSummary)
        {
            return;
        }

        var lastCommentByCaseId = await _db.TaskJournalComments
            .AsNoTracking()
            .Where(comment =>
                comment.TaskJournalItem.ClientCaseId.HasValue &&
                caseIds.Contains(comment.TaskJournalItem.ClientCaseId.Value) &&
                !comment.TaskJournalItem.TagsJson.Contains(CaseInactivityAutomationTag))
            .GroupBy(comment => comment.TaskJournalItem.ClientCaseId!.Value)
            .Select(group => new
            {
                CaseId = group.Key,
                LastAt = group.Max(comment => comment.UpdatedAt ?? comment.CreatedAt),
            })
            .ToDictionaryAsync(item => item.CaseId, item => (DateTime?)item.LastAt, ct);

        var lastDocumentByCaseId = await _db.ClientCaseDocuments
            .AsNoTracking()
            .Where(document => caseIds.Contains(document.ClientCaseId))
            .GroupBy(document => document.ClientCaseId)
            .Select(group => new
            {
                CaseId = group.Key,
                LastAt = group.Max(document => document.UploadedAt),
            })
            .ToDictionaryAsync(item => item.CaseId, item => (DateTime?)item.LastAt, ct);

        var lastCourtEventByCaseId = await _db.ClientCaseCourtEvents
            .AsNoTracking()
            .Where(courtEvent => caseIds.Contains(courtEvent.ClientCaseId))
            .GroupBy(courtEvent => courtEvent.ClientCaseId)
            .Select(group => new
            {
                CaseId = group.Key,
                LastAt = group.Max(courtEvent => courtEvent.UpdatedAt ?? courtEvent.CreatedAt),
            })
            .ToDictionaryAsync(item => item.CaseId, item => (DateTime?)item.LastAt, ct);

        var automationSettings = await _db.SystemSettings
            .AsNoTracking()
            .Where(setting =>
                setting.Key == CaseInactivityAutomationEnabledSettingKey ||
                setting.Key == CaseInactivityAutomationDaysSettingKey)
            .ToDictionaryAsync(setting => setting.Key, setting => setting.Value, ct);
        var automationEnabled = !automationSettings.TryGetValue(
                CaseInactivityAutomationEnabledSettingKey,
                out var automationEnabledRaw) ||
            !bool.TryParse(automationEnabledRaw, out var parsedAutomationEnabled) ||
            parsedAutomationEnabled;
        var inactivityDays = automationSettings.TryGetValue(
                CaseInactivityAutomationDaysSettingKey,
                out var inactivityDaysRaw) &&
            int.TryParse(inactivityDaysRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInactivityDays)
                ? Math.Clamp(parsedInactivityDays, 1, 365)
                : 14;
        var effectiveResponsibleUserIds = cases
            .Select(ResolveEffectiveCaseResponsibleUserId)
            .Where(userId => userId.HasValue)
            .Select(userId => userId!.Value)
            .Distinct()
            .ToArray();
        var vacationUserIds = await CaseInactivityResponsibleAvailabilityService.GetUsersOnVacationAsync(
            _db,
            now,
            effectiveResponsibleUserIds,
            ct);

        foreach (var clientCase in cases)
        {
            summaryByCaseId.TryGetValue(clientCase.Id, out var taskSummary);
            var meaningfulActivity = CaseMeaningfulActivityResolver.Resolve(
                new CaseMeaningfulActivitySignals(
                    clientCase.CreatedAt,
                    taskSummary?.LastMeaningfulTaskAt,
                    lastCommentByCaseId.GetValueOrDefault(clientCase.Id),
                    lastDocumentByCaseId.GetValueOrDefault(clientCase.Id),
                    lastCourtEventByCaseId.GetValueOrDefault(clientCase.Id),
                    clientCase.LastViewedAt,
                    clientCase.ActivityConfirmedAt));
            clientCase.LastMeaningfulActivityAt = meaningfulActivity.At;
            clientCase.NextInactivityControlAt = null;
            clientCase.InactivityControlExclusionReason = string.Empty;

            if (!automationEnabled)
            {
                clientCase.InactivityControlExclusionReason = "Автоконтроль отключён администратором";
                continue;
            }

            if (!clientCase.InactivityRemindersEnabled)
            {
                clientCase.InactivityControlExclusionReason =
                    clientCase.InactivityRemindersDisabledReason.Trim();
                continue;
            }

            if (CaseInactivityControlCyclePolicy.ExcludedCaseStatuses.Contains(clientCase.Status))
            {
                clientCase.InactivityControlExclusionReason =
                    clientCase.Status == ClientCaseStatus.OnHold
                        ? "Дело приостановлено"
                        : "Дело завершено";
                continue;
            }

            var responsibleUserId = ResolveEffectiveCaseResponsibleUserId(clientCase);
            if (responsibleUserId.HasValue && vacationUserIds.Contains(responsibleUserId.Value))
            {
                clientCase.InactivityControlExclusionReason = "Ответственный в отпуске";
                continue;
            }

            if (taskSummary is { ActiveOtherTaskCount: > 0 })
            {
                clientCase.InactivityControlExclusionReason = "Есть активная задача";
                continue;
            }

            if (taskSummary?.ActiveControlNextCheckAt is { } storedNextCheckAt)
            {
                clientCase.NextInactivityControlAt = storedNextCheckAt;
                continue;
            }

            var nextCycleBase = taskSummary?.LastControlCycleEndedAt is { } cycleEndedAt &&
                cycleEndedAt > meaningfulActivity.At
                    ? cycleEndedAt
                    : meaningfulActivity.At;
            clientCase.NextInactivityControlAt = nextCycleBase.AddDays(inactivityDays);
        }
    }

    private static int? ResolveEffectiveCaseResponsibleUserId(ClientCase clientCase)
        => clientCase.ResponsibleUserId
           ?? clientCase.Client?.ResponsibleUserId
           ?? clientCase.LegalEntity?.ResponsibleUserId;

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
        CaseDocumentTemplateDefinition definition,
        TimeZoneInfo timeZone)
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
            BuildCaseDocumentTemplateHtml(clientCase, definition, documentType, timeZone));
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

    private async Task<ClientCaseCourtSyncSettingsDto> ResolveCourtSyncSettingsAsync(CancellationToken ct)
    {
        var settings = await _db.SystemSettings
            .AsNoTracking()
            .Where(item =>
                item.Key == ClientCaseCourtSyncSchedulerService.EnabledSettingKey ||
                item.Key == ClientCaseCourtSyncSchedulerService.IntervalMinutesSettingKey ||
                item.Key == ClientCaseCourtSyncSchedulerService.BatchSizeSettingKey)
            .ToDictionaryAsync(item => item.Key, item => item.Value, ct);

        var configEnabled = _configuration.GetValue("LegalIntegrations:CourtSyncScheduler:Enabled", false);
        var configInterval = _configuration.GetValue<int?>("LegalIntegrations:CourtSyncScheduler:IntervalMinutes")
            ?? ClientCaseCourtSyncSchedulerService.DefaultIntervalMinutes;
        var configBatchSize = _configuration.GetValue<int?>("LegalIntegrations:CourtSyncScheduler:BatchSize")
            ?? ClientCaseCourtSyncSchedulerService.DefaultBatchSize;

        return new ClientCaseCourtSyncSettingsDto
        {
            IsEnabled = ParseBoolSetting(settings.GetValueOrDefault(ClientCaseCourtSyncSchedulerService.EnabledSettingKey), configEnabled),
            FeatureFlagEnabled = _featureFlags?.CurrentValue.Legal.ExternalCourtSync ?? false,
            IntervalMinutes = ClientCaseCourtSyncSchedulerService.NormalizeIntervalMinutes(
                ParseIntSetting(settings.GetValueOrDefault(ClientCaseCourtSyncSchedulerService.IntervalMinutesSettingKey), configInterval)),
            BatchSize = ClientCaseCourtSyncSchedulerService.NormalizeBatchSize(
                ParseIntSetting(settings.GetValueOrDefault(ClientCaseCourtSyncSchedulerService.BatchSizeSettingKey), configBatchSize)),
            MinIntervalMinutes = ClientCaseCourtSyncSchedulerService.MinIntervalMinutes,
            MaxIntervalMinutes = ClientCaseCourtSyncSchedulerService.MaxIntervalMinutes,
            MinBatchSize = ClientCaseCourtSyncSchedulerService.MinBatchSize,
            MaxBatchSize = ClientCaseCourtSyncSchedulerService.MaxBatchSize,
        };
    }

    private ICourtSyncProvider? ResolveCourtSyncProvider(string? sourceUrl)
    {
        var provider = _courtSyncProviderRegistry?.Resolve(sourceUrl);
        if (provider is not null)
        {
            return provider;
        }

        return _sudrfCourtSyncService is not null && _sudrfCourtSyncService.CanHandle(sourceUrl)
            ? new SudrfCourtSyncProvider(_sudrfCourtSyncService)
            : null;
    }

    private async Task UpsertSystemSettingAsync(string key, string value, DateTime nowUtc, CancellationToken ct)
    {
        var setting = await _db.SystemSettings.FirstOrDefaultAsync(item => item.Key == key, ct);
        if (setting is null)
        {
            _db.SystemSettings.Add(new SystemSetting
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

    private static bool ParseBoolSetting(string? value, bool fallback)
        => bool.TryParse(value, out var parsed) ? parsed : fallback;

    private static int ParseIntSetting(string? value, int fallback)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private async Task<ProductionCalendarSettingsStorage> GetProductionCalendarSettingsAsync(CancellationToken ct)
    {
        var rawJson = await _db.SystemSettings
            .AsNoTracking()
            .Where(setting => setting.Key == CaseProductionCalendarSettingKey)
            .Select(setting => setting.Value)
            .FirstOrDefaultAsync(ct);

        return NormalizeProductionCalendarSettings(DeserializeProductionCalendarSettings(rawJson));
    }

    private static ProductionCalendarSettingsStorage? DeserializeProductionCalendarSettings(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ProductionCalendarSettingsStorage>(rawJson, CaseDocumentTemplateJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static (ProductionCalendarSettingsStorage Settings, string? Error) BuildProductionCalendarSettings(
        UpsertProceduralDeadlineCalendarSettingsRequest? request)
    {
        var workingDates = NormalizeProductionCalendarDates(request?.WorkingDates, out var workingError);
        if (workingError is not null)
        {
            return (new ProductionCalendarSettingsStorage(), workingError);
        }

        var nonWorkingDates = NormalizeProductionCalendarDates(request?.NonWorkingDates, out var nonWorkingError);
        if (nonWorkingError is not null)
        {
            return (new ProductionCalendarSettingsStorage(), nonWorkingError);
        }

        var workingKeys = workingDates.Select(static item => item.Date).ToHashSet(StringComparer.Ordinal);
        var duplicate = nonWorkingDates.FirstOrDefault(item => workingKeys.Contains(item.Date));
        if (duplicate is not null)
        {
            return (new ProductionCalendarSettingsStorage(), $"Дата {duplicate.Date} не может быть одновременно рабочей и нерабочей.");
        }

        return (NormalizeProductionCalendarSettings(new ProductionCalendarSettingsStorage
        {
            WorkingDates = workingDates,
            NonWorkingDates = nonWorkingDates,
        }), null);
    }

    private static ProductionCalendarSettingsStorage NormalizeProductionCalendarSettings(ProductionCalendarSettingsStorage? storage)
        => new()
        {
            WorkingDates = NormalizeProductionCalendarDates(storage?.WorkingDates, out _),
            NonWorkingDates = NormalizeProductionCalendarDates(storage?.NonWorkingDates, out _),
            UpdatedAt = storage?.UpdatedAt,
        };

    private static List<ProductionCalendarDateStorage> NormalizeProductionCalendarDates(
        IEnumerable<ProceduralDeadlineCalendarDateRequest>? dates,
        out string? error)
        => NormalizeProductionCalendarDates(
            dates?.Select(static item => new ProductionCalendarDateStorage { Date = item.Date, Note = item.Note }),
            out error);

    private static List<ProductionCalendarDateStorage> NormalizeProductionCalendarDates(
        IEnumerable<ProductionCalendarDateStorage>? dates,
        out string? error)
    {
        error = null;
        var result = new Dictionary<string, ProductionCalendarDateStorage>(StringComparer.Ordinal);
        foreach (var date in dates ?? [])
        {
            if (!TryNormalizeProductionCalendarDate(date.Date, out var key))
            {
                error = "Укажите даты производственного календаря в формате ГГГГ-ММ-ДД.";
                return [];
            }

            result[key] = new ProductionCalendarDateStorage
            {
                Date = key,
                Note = NormalizeOptional(date.Note),
            };
        }

        return result.Values.OrderBy(static item => item.Date, StringComparer.Ordinal).ToList();
    }

    private static bool TryNormalizeProductionCalendarDate(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var source = value.Trim();
        if (source.Length >= 10)
        {
            source = source[..10];
        }

        if (!DateTime.TryParseExact(source, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return false;
        }

        normalized = parsed.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return true;
    }

    private static List<DateTime> MergeCalculationDates(
        IReadOnlyCollection<DateTime>? requestDates,
        IReadOnlyCollection<ProductionCalendarDateStorage> settingDates)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var date in requestDates ?? [])
        {
            result.Add(date.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        foreach (var date in settingDates)
        {
            result.Add(date.Date);
        }

        return result
            .OrderBy(static item => item, StringComparer.Ordinal)
            .Select(static item => DateTime.SpecifyKind(DateTime.ParseExact(item, "yyyy-MM-dd", CultureInfo.InvariantCulture), DateTimeKind.Utc))
            .ToList();
    }

    private static ProceduralDeadlineCalendarSettingsDto MapProductionCalendarSettings(ProductionCalendarSettingsStorage storage)
        => new()
        {
            WorkingDates = storage.WorkingDates
                .Select(static item => new ProceduralDeadlineCalendarDateDto { Date = item.Date, Note = item.Note })
                .ToList(),
            NonWorkingDates = storage.NonWorkingDates
                .Select(static item => new ProceduralDeadlineCalendarDateDto { Date = item.Date, Note = item.Note })
                .ToList(),
            UpdatedAt = storage.UpdatedAt,
        };

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
        string documentType,
        TimeZoneInfo timeZone)
    {
        var ownerName = ResolveCaseOwnerName(clientCase);
        var responsibleName = clientCase.ResponsibleUser?.FullName ?? string.Empty;
        var now = CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone);
        if (!string.IsNullOrWhiteSpace(definition.Content))
        {
            return BuildCustomCaseDocumentTemplateHtml(clientCase, definition, ownerName, responsibleName, documentType, now, timeZone);
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
        AppendTemplateRow(html, "Ближайшее заседание", FormatTemplateDateTime(clientCase.NextHearingAt, timeZone));
        AppendTemplateRow(html, "Дата подачи заявления", FormatTemplateDate(clientCase.ClaimFiledAt));
        AppendTemplateRow(html, "Дата решения суда", FormatTemplateDate(clientCase.CourtDecisionAt));
        AppendTemplateRow(html, "Срок апелляции", FormatTemplateDate(clientCase.AppealDeadlineAt));
        AppendTemplateRow(html, "Срок кассации", FormatTemplateDate(clientCase.CassationDeadlineAt));
        html.AppendLine("    </table>");

        html.AppendLine("    <div class=\"body\">");
        html.AppendLine("      <h2>Текст документа</h2>");
        foreach (var paragraph in BuildTemplateParagraphs(definition.Key, clientCase, ownerName, documentType, timeZone))
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
        DateTime now,
        TimeZoneInfo timeZone)
    {
        var renderedContent = ReplaceCaseDocumentTemplateTokens(
            definition.Content ?? string.Empty,
            clientCase,
            ownerName,
            responsibleName,
            documentType,
            now,
            timeZone);
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
        DateTime now,
        TimeZoneInfo timeZone)
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
            ["{{nextHearingAt}}"] = FormatTemplateDateTime(clientCase.NextHearingAt, timeZone),
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
        string documentType,
        TimeZoneInfo timeZone)
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
                $"Документ связан со стадией «{ValueOrDash(clientCase.Stage)}», ближайшее заседание: {ValueOrDash(FormatTemplateDateTime(clientCase.NextHearingAt, timeZone))}.",
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

    private static string FormatTemplateDateTime(DateTime? value, TimeZoneInfo timeZone)
        => value.HasValue ? CalendarTimeZoneHelper.FormatUserDateTime(value.Value, timeZone) : string.Empty;

    private static string ValueOrDash(string? value)
        => string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();

    private static string E(string? value)
        => WebUtility.HtmlEncode(value ?? string.Empty);

    private bool IsCaseDocumentAvailable(ClientCaseDocument document)
    {
        try
        {
            return _caseDocumentStorage.Exists(document.StoragePath);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
            or ArgumentException
            or NotSupportedException)
        {
            return false;
        }
    }

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
            SourceType = document.SourceType,
            SourceUrl = document.SourceUrl,
            SourceCheckedAt = document.SourceCheckedAt.HasValue
                ? CalendarTimeZoneHelper.NormalizeUtcInstant(document.SourceCheckedAt.Value)
                : null,
            SourceConfidence = document.SourceConfidence,
            UploadedByUserId = document.UploadedByUserId,
            UploadedByName = document.UploadedByName,
            UploadedAt = CalendarTimeZoneHelper.NormalizeUtcInstant(document.UploadedAt),
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

    private static string BuildWorkOrderActDocumentKey(int actId)
        => $"act-{actId}";

    private static string BuildWorkOrderActLineSourceKey(int workOrderId, int workOrderItemId)
        => $"work-order:{workOrderId}:item:{workOrderItemId}";

    private static string BuildWorkOrderContractDocumentKey(int contractId)
        => $"contract-{contractId}";

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

    private static ClientCaseSummaryDto MapCaseSummary(ClientCase clientCase, bool canViewFinance = true)
    {
        if (!canViewFinance)
        {
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
                FeeAmount = null,
                PaidAmount = 0m,
                DebtAmount = 0m,
                LastTaskAt = clientCase.LastTaskAt,
                LastViewedAt = clientCase.LastViewedAt,
                LastViewedByResponsibleAt = clientCase.LastViewedAt,
                LastViewedByResponsibleUserId = clientCase.LastViewedByUserId,
                LastViewedByResponsibleUserName = clientCase.LastViewedByUserName,
            };
        }

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
            LastViewedByResponsibleAt = clientCase.LastViewedAt,
            LastViewedByResponsibleUserId = clientCase.LastViewedByUserId,
            LastViewedByResponsibleUserName = clientCase.LastViewedByUserName,
        };
    }

    private static void PopulateResponsibleViewAliases(IEnumerable<ClientCase> cases)
    {
        foreach (var clientCase in cases)
        {
            clientCase.LastViewedByResponsibleAt = clientCase.LastViewedAt;
            clientCase.LastViewedByResponsibleUserId = clientCase.LastViewedByUserId;
            clientCase.LastViewedByResponsibleUserName = clientCase.LastViewedByUserName;
        }
    }

    private static ClientCaseAccessRequestListItemDto MapClientCaseAccessRequest(ClientCaseAccessRequestRow clientCase)
        => new()
        {
            ClientCaseId = clientCase.ClientCaseId,
            CaseNumber = clientCase.CaseNumber,
            Title = clientCase.Title,
            OwnerName = clientCase.ClientName
                ?? clientCase.LegalEntityShortName
                ?? clientCase.LegalEntityFullName
                ?? string.Empty,
            ResponsibleUserId = clientCase.ResponsibleUserId,
            ResponsibleName = FormatCaseResponsible(clientCase.ResponsibleFullName, clientCase.ResponsibleEmail),
            RequestedAt = clientCase.RequestedAt,
            RequestedByUserId = clientCase.AccessRequestedByUserId,
            RequestedByUserName = clientCase.AccessRequestedByUserName,
            Reason = clientCase.Reason,
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

    private static ClientCaseFinanceCalendarDto BuildFinanceCalendar(IReadOnlyCollection<Payment> payments, DateTime today)
    {
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
        IReadOnlyDictionary<int, Contract> contractsById,
        DateTime today)
    {
        return payments
            .Where(IsFinanceInvoiceCandidate)
            .GroupBy(BuildFinanceInvoiceKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => MapFinanceInvoice(group.ToList(), contractsById, today))
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
        IReadOnlyDictionary<int, Contract> contractsById,
        DateTime today)
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
            Status = ResolveFinanceGroupStatus(group, today),
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

    private static string ResolveFinanceGroupStatus(IReadOnlyCollection<Payment> payments, DateTime today)
    {
        if (payments.Any(payment => ResolveFinancePaymentDisplayStatus(payment, today) == PaymentStatus.Overdue))
        {
            return PaymentStatus.Overdue.ToString();
        }

        if (payments.All(payment => ResolveFinancePaymentDisplayStatus(payment, today) == PaymentStatus.Completed))
        {
            return PaymentStatus.Completed.ToString();
        }

        if (payments.Any(payment => payment.Status == PaymentStatus.Processing))
        {
            return PaymentStatus.Processing.ToString();
        }

        return payments.First().Status.ToString();
    }

    private static ClientCaseFinancePaymentDto MapFinancePayment(Payment payment, DateTime today)
        => new()
        {
            Id = payment.Id,
            DocumentKind = payment.DocumentKind.ToString(),
            Type = payment.Type.ToString(),
            Amount = payment.Amount,
            PaidAmount = payment.PaidAmount,
            OutstandingAmount = ResolveFinancePaymentOutstanding(payment),
            Status = ResolveFinancePaymentDisplayStatus(payment, today).ToString(),
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

    private static PaymentStatus ResolveFinancePaymentDisplayStatus(Payment payment, DateTime today)
    {
        if (payment.Status is PaymentStatus.Completed or PaymentStatus.Cancelled)
        {
            return payment.Status;
        }

        if (ResolveFinancePaymentOutstanding(payment) <= 0m)
        {
            return PaymentStatus.Completed;
        }

        var plannedDate = ResolveFinancePaymentCalendarDate(payment).Date;
        return payment.Status == PaymentStatus.Overdue || plannedDate < today
            ? PaymentStatus.Overdue
            : payment.Status;
    }

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

    private static string BuildWorkOrderPrintHtml(ClientCase clientCase, ClientCaseWorkOrder workOrder, DateTime generatedAtLocal)
    {
        RecalculateWorkOrder(workOrder);
        var generatedAt = FormatUserGeneratedAt(generatedAtLocal);
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
        DateTime dueDate,
        DateTime generatedAtLocal)
    {
        var generatedAt = FormatUserGeneratedAt(generatedAtLocal);
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

    private static string BuildWorkOrderActDocumentHtml(
        ClientCase clientCase,
        ClientCaseWorkOrder workOrder,
        Act act,
        DateTime generatedAtLocal)
    {
        var generatedAt = FormatUserGeneratedAt(generatedAtLocal);
        var rows = string.Join(Environment.NewLine, act.LineItems
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
            rows = """<tr><td colspan="6" class="empty">Позиции акта пока не добавлены.</td></tr>""";
        }

        return $$"""
<!doctype html>
<html lang="ru">
<head>
  <meta charset="utf-8" />
  <title>Акт №{{Html(act.Number)}}</title>
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
    <h1>Акт №{{Html(act.Number)}}</h1>
    <div class="meta">Сформировано: {{Html(generatedAt)}}</div>
    <div class="grid">
      <div class="stat"><span>Дело</span><strong>{{Html(clientCase.Title)}}</strong><div class="meta">{{Html(clientCase.CaseNumber)}}</div></div>
      <div class="stat"><span>Клиент</span><strong>{{Html(GetOwnerName(clientCase))}}</strong></div>
      <div class="stat"><span>Основание</span><strong>{{Html(workOrder.Title)}}</strong></div>
      <div class="stat"><span>Дата акта</span><strong>{{Html(FormatRuDate(act.Date))}}</strong></div>
      <div class="stat"><span>Статус</span><strong>Черновик</strong></div>
      <div class="stat"><span>Сумма</span><strong>{{Html(FormatMoney(act.Amount))}}</strong></div>
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
    <div class="total">Итого: {{Html(FormatMoney(act.Amount))}}</div>
  </main>
</body>
</html>
""";
    }

    private static string BuildWorkOrderContractDescription(
        ClientCase clientCase,
        ClientCaseWorkOrder workOrder,
        IReadOnlyCollection<ClientCaseWorkOrderItem> items)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Договор сформирован из заказ-наряда «{workOrder.Title}» по делу {clientCase.Title}.");
        if (!string.IsNullOrWhiteSpace(clientCase.CaseNumber))
        {
            builder.AppendLine($"Номер дела: {clientCase.CaseNumber}.");
        }

        builder.AppendLine();
        builder.AppendLine("Услуги:");
        foreach (var item in items.OrderBy(item => item.SortOrder).ThenBy(item => item.Id))
        {
            builder.AppendLine($"- {item.ServiceName}: {item.Quantity:N2} шт. x {FormatMoney(item.UnitPrice)} = {FormatMoney(item.Quantity * item.UnitPrice)}");
        }

        builder.AppendLine();
        builder.AppendLine($"Итого: {FormatMoney(workOrder.TotalAmount)}.");
        return builder.ToString().Trim();
    }

    private static string BuildWorkOrderContractDocumentHtml(
        ClientCase clientCase,
        ClientCaseWorkOrder workOrder,
        Contract contract,
        IReadOnlyCollection<ClientCaseWorkOrderItem> items,
        DateTime generatedAtLocal)
    {
        var generatedAt = FormatUserGeneratedAt(generatedAtLocal);
        var rows = string.Join(Environment.NewLine, items
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Id)
            .Select((item, index) => $"""
              <tr>
                <td>{index + 1}</td>
                <td>{Html(item.ServiceName)}</td>
                <td class="num">{Html(item.Quantity.ToString("N2", CultureInfo.GetCultureInfo("ru-RU")))}</td>
                <td>шт</td>
                <td class="num">{Html(FormatMoney(item.UnitPrice))}</td>
                <td class="num">{Html(FormatMoney(item.Quantity * item.UnitPrice))}</td>
              </tr>
            """));
        if (string.IsNullOrWhiteSpace(rows))
        {
            rows = """<tr><td colspan="6" class="empty">Услуги договора пока не добавлены.</td></tr>""";
        }

        var validUntil = contract.IsOpenEnded
            ? "Бессрочно"
            : FormatRuDate(contract.ValidUntil);

        return $$"""
<!doctype html>
<html lang="ru">
<head>
  <meta charset="utf-8" />
  <title>Договор №{{Html(contract.Number)}}</title>
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
    .description { margin-top: 18px; white-space: pre-wrap; color: #334155; font-size: 13px; line-height: 1.55; }
    @media print { body { padding: 0; background: #fff; } main { border: 0; border-radius: 0; } }
  </style>
</head>
<body>
  <main>
    <h1>Договор №{{Html(contract.Number)}}</h1>
    <div class="meta">Сформировано: {{Html(generatedAt)}}</div>
    <div class="grid">
      <div class="stat"><span>Дело</span><strong>{{Html(clientCase.Title)}}</strong><div class="meta">{{Html(clientCase.CaseNumber)}}</div></div>
      <div class="stat"><span>Клиент</span><strong>{{Html(GetOwnerName(clientCase))}}</strong></div>
      <div class="stat"><span>Основание</span><strong>{{Html(workOrder.Title)}}</strong></div>
      <div class="stat"><span>Дата договора</span><strong>{{Html(FormatRuDate(contract.Date))}}</strong></div>
      <div class="stat"><span>Действует до</span><strong>{{Html(validUntil)}}</strong></div>
      <div class="stat"><span>Сумма</span><strong>{{Html(FormatMoney(contract.Amount ?? 0m))}}</strong></div>
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
    <div class="total">Итого: {{Html(FormatMoney(contract.Amount ?? 0m))}}</div>
    <div class="description">{{Html(contract.Description)}}</div>
  </main>
</body>
</html>
""";
    }

    private static ClientCaseProceduralDeadlineDto MapProceduralDeadline(ClientCaseProceduralDeadline item, CaseStatusClock clock)
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
            SourceUrl = item.SourceUrl,
            SourceCheckedAt = item.SourceCheckedAt.HasValue
                ? CalendarTimeZoneHelper.NormalizeUtcInstant(item.SourceCheckedAt.Value)
                : null,
            SourceConfidence = item.SourceConfidence,
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
            IsOverdue = item.Status is not "closed" and not "cancelled" and not "archived" && IsCaseStatusOverdue(item.DueAt, item.AllDay, clock),
            IsWarning = item.Status is not "closed" and not "cancelled" and not "archived" && IsCaseStatusInWarningPeriod(item.DueAt, item.AllDay, Math.Max(0, item.WarningDaysBefore), clock),
        };

    private static ClientCaseCourtEventDto MapCourtEvent(ClientCaseCourtEvent item, CaseStatusClock clock)
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
            SourceUrl = item.SourceUrl,
            SourceCheckedAt = item.SourceCheckedAt.HasValue
                ? CalendarTimeZoneHelper.NormalizeUtcInstant(item.SourceCheckedAt.Value)
                : null,
            SourceConfidence = item.SourceConfidence,
            ClientCaseDocumentId = item.ClientCaseDocumentId,
            ProceduralDeadlineId = item.ProceduralDeadlineId,
            CreatedAt = item.CreatedAt,
            CreatedByUserId = item.CreatedByUserId,
            CreatedByUserName = item.CreatedByUserName,
            UpdatedAt = item.UpdatedAt,
            UpdatedByUserId = item.UpdatedByUserId,
            UpdatedByUserName = item.UpdatedByUserName,
            IsOverdue = item.MovementStatus is not "held" and not "completed" and not "cancelled" and not "archived" && IsCaseStatusOverdue(item.ScheduledAt, item.AllDay, clock),
        };

    private static bool IsCaseStatusOverdue(DateTime value, bool allDay, CaseStatusClock clock)
        => allDay
            ? value.Date < clock.TodayDate
            : value < clock.NowInstantUtc;

    private static bool IsCaseStatusInWarningPeriod(DateTime value, bool allDay, int warningDays, CaseStatusClock clock)
    {
        var normalizedWarningDays = Math.Max(0, warningDays);
        return allDay
            ? value.Date >= clock.TodayDate && value.Date <= clock.TodayDate.AddDays(normalizedWarningDays)
            : value >= clock.NowInstantUtc && value <= clock.NowInstantUtc.AddDays(normalizedWarningDays);
    }

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

    private static ClientCaseCourtInstanceDto MapCourtInstance(ClientCaseCourtInstance item)
        => new()
        {
            Id = item.Id,
            ClientCaseId = item.ClientCaseId,
            Instance = item.Instance,
            InstanceLabel = ResolveCourtInstanceLabel(item.Instance),
            CourtId = item.CourtId,
            CourtName = item.CourtName,
            CourtCaseNumber = item.CourtCaseNumber,
            CourtUrl = item.CourtUrl,
            JudgeName = item.JudgeName,
            Courtroom = item.Courtroom,
            SecretaryName = item.SecretaryName,
            Notes = item.Notes,
            CreatedAt = item.CreatedAt,
            CreatedByUserId = item.CreatedByUserId,
            CreatedByUserName = item.CreatedByUserName,
            UpdatedAt = item.UpdatedAt,
            UpdatedByUserId = item.UpdatedByUserId,
            UpdatedByUserName = item.UpdatedByUserName,
            Complaints = item.Complaints
                .Where(complaint => !complaint.IsArchived)
                .OrderByDescending(complaint => complaint.FiledAt ?? complaint.CreatedAt)
                .ThenByDescending(complaint => complaint.Id)
                .Select(MapCourtComplaint)
                .ToList(),
        };

    private static ClientCaseCourtComplaintDto MapCourtComplaint(ClientCaseCourtComplaint item)
        => new()
        {
            Id = item.Id,
            ClientCaseId = item.ClientCaseId,
            CourtInstanceId = item.CourtInstanceId,
            ComplaintType = item.ComplaintType,
            ComplaintTypeLabel = ResolveCourtComplaintTypeLabel(item.ComplaintType),
            Title = item.Title,
            TargetInstance = item.TargetInstance,
            TargetInstanceLabel = ResolveCourtInstanceLabel(item.TargetInstance),
            FiledAt = item.FiledAt,
            Status = item.Status,
            StatusLabel = ResolveCourtComplaintStatusLabel(item.Status),
            ClientCaseDocumentId = item.ClientCaseDocumentId,
            Notes = item.Notes,
            IsArchived = item.IsArchived,
            CreatedAt = item.CreatedAt,
            CreatedByUserId = item.CreatedByUserId,
            CreatedByUserName = item.CreatedByUserName,
            UpdatedAt = item.UpdatedAt,
            UpdatedByUserId = item.UpdatedByUserId,
            UpdatedByUserName = item.UpdatedByUserName,
        };

    private static ClientCaseCourtInstance BuildDefaultCourtInstance(ClientCase clientCase, string instance)
    {
        var normalizedInstance = NormalizeCourtInstance(instance);
        var isFirstInstance = normalizedInstance == "first";
        return new ClientCaseCourtInstance
        {
            ClientCaseId = clientCase.Id,
            Instance = normalizedInstance,
            CourtName = isFirstInstance ? clientCase.CourtName : string.Empty,
            CourtCaseNumber = isFirstInstance ? clientCase.CourtCaseNumber : string.Empty,
            CourtUrl = isFirstInstance ? clientCase.CourtUrl : string.Empty,
            JudgeName = isFirstInstance ? clientCase.JudgeName : string.Empty,
            Courtroom = isFirstInstance ? clientCase.Courtroom : string.Empty,
            SecretaryName = isFirstInstance ? clientCase.CourtSecretaryContacts : string.Empty,
            Complaints = [],
        };
    }

    private async Task<ClientCaseCourtInstance> EnsureCourtInstanceAsync(
        ClientCase clientCase,
        string instance,
        int? actorUserId,
        string actorName,
        CancellationToken ct)
    {
        var normalizedInstance = NormalizeCourtInstance(instance);
        var courtInstance = await _db.ClientCaseCourtInstances
            .FirstOrDefaultAsync(item => item.ClientCaseId == clientCase.Id && item.Instance == normalizedInstance, ct);
        if (courtInstance is not null)
        {
            return courtInstance;
        }

        var defaults = BuildDefaultCourtInstance(clientCase, normalizedInstance);
        var now = DateTime.UtcNow;
        courtInstance = new ClientCaseCourtInstance
        {
            ClientCaseId = clientCase.Id,
            Instance = normalizedInstance,
            CourtName = defaults.CourtName,
            CourtCaseNumber = defaults.CourtCaseNumber,
            CourtUrl = defaults.CourtUrl,
            JudgeName = defaults.JudgeName,
            Courtroom = defaults.Courtroom,
            SecretaryName = defaults.SecretaryName,
            CreatedAt = now,
            CreatedByUserId = actorUserId,
            CreatedByUserName = Truncate(actorName, 256),
            UpdatedAt = now,
            UpdatedByUserId = actorUserId,
            UpdatedByUserName = Truncate(actorName, 256),
        };
        _db.ClientCaseCourtInstances.Add(courtInstance);
        await _db.SaveChangesAsync(ct);
        return courtInstance;
    }

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
            SourceType = item.SourceType,
            SourceUrl = item.SourceUrl,
            SourceCheckedAt = item.SourceCheckedAt.HasValue
                ? CalendarTimeZoneHelper.NormalizeUtcInstant(item.SourceCheckedAt.Value)
                : null,
            SourceConfidence = item.SourceConfidence,
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
        => CourtSyncNormalization.GetPartySideLabel(value);

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

    private static CourtInstanceValidationResult ValidateCourtInstanceRequest(
        UpsertClientCaseCourtInstanceRequest? request,
        ClientCase clientCase,
        string instance,
        ClientCaseCourt? court)
    {
        if (request is null)
        {
            return new("Укажите параметры судебной инстанции.");
        }

        var defaults = BuildDefaultCourtInstance(clientCase, instance);
        return new(
            null,
            Truncate(NormalizeOptional(request.CourtName) ?? NormalizeOptional(court?.Name) ?? defaults.CourtName, 200),
            Truncate(NormalizeOptional(request.CourtCaseNumber) ?? defaults.CourtCaseNumber, 100),
            Truncate(NormalizeOptional(request.CourtUrl) ?? NormalizeOptional(court?.Url) ?? defaults.CourtUrl, 1000),
            Truncate(NormalizeOptional(request.JudgeName) ?? defaults.JudgeName, 200),
            Truncate(NormalizeOptional(request.Courtroom) ?? defaults.Courtroom, 120),
            Truncate(NormalizeOptional(request.SecretaryName) ?? defaults.SecretaryName, 200),
            Truncate(NormalizeOptional(request.Notes) ?? string.Empty, 1000));
    }

    private async Task<CourtComplaintValidationResult> ValidateCourtComplaintRequestAsync(
        int clientCaseId,
        string instance,
        UpsertClientCaseCourtComplaintRequest? request,
        CancellationToken ct)
    {
        if (request is null)
        {
            return new("Укажите параметры жалобы.");
        }

        if (request.ClientCaseDocumentId is { } documentId)
        {
            var documentExists = await _db.ClientCaseDocuments
                .AsNoTracking()
                .AnyAsync(item => item.Id == documentId && item.ClientCaseId == clientCaseId, ct);
            if (!documentExists)
            {
                return new("Документ жалобы не найден в этом деле.");
            }
        }

        var complaintType = NormalizeCourtComplaintType(request.ComplaintType);
        var title = NormalizeOptional(request.Title) ?? ResolveCourtComplaintTypeLabel(complaintType);
        return new(
            null,
            complaintType,
            Truncate(title, 200),
            NormalizeCourtInstance(request.TargetInstance ?? ResolveNextCourtInstance(instance)),
            request.FiledAt,
            NormalizeCourtComplaintStatus(request.Status),
            request.ClientCaseDocumentId,
            Truncate(NormalizeOptional(request.Notes) ?? string.Empty, 1000));
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
        => CourtSyncNormalization.NormalizeEventType(value);

    private static string ResolveCourtEventTypeLabel(string value)
        => CourtSyncNormalization.GetEventTypeLabel(value);

    private static string NormalizeCourtInstance(string? value)
        => CourtSyncNormalization.NormalizeInstance(value);

    private static string ResolveCourtInstanceLabel(string value)
        => CourtSyncNormalization.GetInstanceLabel(value);

    private static string ResolveNextCourtInstance(string value)
        => NormalizeCourtInstance(value) switch
        {
            "first" => "appeal",
            "appeal" => "cassation",
            "cassation" => "supreme",
            _ => "supreme",
        };

    private static string NormalizeCourtComplaintType(string? value)
    {
        var normalized = NormalizeOptional(value)?.ToLowerInvariant();
        return normalized switch
        {
            "appealcomplaint" or "appeal-complaint" or "appeal_complaint" => "appealComplaint",
            "privatecomplaint" or "private-complaint" or "private_complaint" => "privateComplaint",
            "cassationcomplaint" or "cassation-complaint" or "cassation_complaint" => "cassationComplaint",
            "supervisorycomplaint" or "supervisory-complaint" or "supervisory_complaint" => "supervisoryComplaint",
            _ => "other",
        };
    }

    private static string ResolveCourtComplaintTypeLabel(string value)
        => value switch
        {
            "appealComplaint" => "Апелляционная жалоба",
            "privateComplaint" => "Частная жалоба",
            "cassationComplaint" => "Кассационная жалоба",
            "supervisoryComplaint" => "Жалоба в Верховный суд",
            _ => "Иная жалоба",
        };

    private static string NormalizeCourtComplaintStatus(string? value)
    {
        var normalized = NormalizeOptional(value)?.ToLowerInvariant();
        return normalized switch
        {
            "draft" or "filed" or "accepted" or "returned" or "satisfied" or "rejected" => normalized,
            _ => "draft",
        };
    }

    private static string ResolveCourtComplaintStatusLabel(string value)
        => value switch
        {
            "filed" => "Подана",
            "accepted" => "Принята",
            "returned" => "Возвращена",
            "satisfied" => "Удовлетворена",
            "rejected" => "Отклонена",
            _ => "Черновик",
        };

    private static string NormalizeCourtMovementStatus(string? value)
        => CourtSyncNormalization.NormalizeMovementStatus(value);

    private static string ResolveCourtMovementStatusLabel(string value)
        => CourtSyncNormalization.GetMovementStatusLabel(value);

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
            "archived" => "В архиве",
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

    private async Task<string> BuildWorkOrderActNumberAsync(
        int clientCaseId,
        int workOrderId,
        DateTime actDate,
        string? requestedNumber,
        CancellationToken ct)
    {
        var baseNumber = NormalizeOptional(requestedNumber) ?? $"WO-ACT-{clientCaseId}-{workOrderId}";
        var candidate = baseNumber.Length > 120 ? baseNumber[..120] : baseNumber;
        var suffix = 1;
        while (await _db.Acts.AsNoTracking().AnyAsync(
            act => act.Number == candidate && act.Date.Date == actDate.Date,
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

    private async Task<string> BuildWorkOrderContractNumberAsync(
        int clientCaseId,
        int workOrderId,
        DateTime contractDate,
        string? requestedNumber,
        CancellationToken ct)
    {
        var baseNumber = NormalizeOptional(requestedNumber) ?? $"WO-CONTRACT-{clientCaseId}-{workOrderId}";
        var candidate = baseNumber.Length > 120 ? baseNumber[..120] : baseNumber;
        var suffix = 1;
        while (await _db.Contracts.AsNoTracking().AnyAsync(
            contract => contract.Number == candidate && contract.Date.Date == contractDate.Date,
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

    private static ClientCaseRelatedPartyDto MapRelatedParty(ClientCaseRelatedParty party, bool canRevealSensitiveData = true)
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
            Phone = canRevealSensitiveData ? party.Phone : string.Empty,
            Email = canRevealSensitiveData ? party.Email : string.Empty,
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

    private static string NormalizeCourtSyncText(string? value)
    {
        var normalized = NormalizeOptional(value);
        if (normalized is null)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(normalized.Length);
        var previousWasWhitespace = false;
        foreach (var ch in normalized)
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

            builder.Append(char.ToLowerInvariant(ch));
            previousWasWhitespace = false;
        }

        return builder.ToString().Trim();
    }

    private static bool SameNormalizedCourtSyncText(string? left, string? right)
    {
        var normalizedLeft = NormalizeCourtSyncText(left);
        var normalizedRight = NormalizeCourtSyncText(right);
        return normalizedLeft.Length > 0
            && normalizedRight.Length > 0
            && string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
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

    private void AppendCaseEvent(ClientCase clientCase, string eventText)
    {
        AppendCaseEvent(clientCase, eventText, ResolveCaseStatusClock().NowLocal);
    }

    private static void AppendCaseEvent(ClientCase clientCase, string eventText, DateTime localNow)
    {
        var line = $"{FormatUserGeneratedAt(localNow)}: {eventText}";
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

    private static ClientCaseTimelineEntryDto MapTimelineEntry(int clientCaseId, string line, int index, TimeZoneInfo timeZone)
    {
        var occurredAt = TryParseCaseHistoryTimestamp(line, timeZone, out var parsed)
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

    private async Task PopulateConversationAttachmentsAsync(
        IReadOnlyList<ClientCaseConversationEntryDto> entries,
        CancellationToken ct)
    {
        var documentIds = entries
            .SelectMany(static entry => entry.Attachments)
            .Select(static attachment => attachment.DocumentId)
            .Distinct()
            .ToList();
        if (documentIds.Count == 0)
        {
            return;
        }

        var documents = await _db.ClientCaseDocuments
            .AsNoTracking()
            .Where(item => documentIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, ct);
        foreach (var entry in entries)
        {
            if (entry.Attachments.Count == 0)
            {
                continue;
            }

            entry.Attachments = entry.Attachments
                .Select(attachment => documents.TryGetValue(attachment.DocumentId, out var document)
                    ? MapConversationAttachment(document)
                    : attachment)
                .ToList();
        }
    }

    private static ClientCaseConversationAttachmentDto MapConversationAttachment(ClientCaseDocument document)
        => new()
        {
            DocumentId = document.Id,
            FileName = document.OriginalFileName,
            ContentType = document.ContentType,
            SizeBytes = document.SizeBytes,
            UploadedByName = document.UploadedByName,
            UploadedAt = document.UploadedAt,
        };

    private static (string? Text, string? Error) NormalizeConversationMessage(string? text)
    {
        var message = NormalizeOptional(text);
        if (message is null)
        {
            return (null, "Введите текст сообщения по делу.");
        }

        message = NormalizeHistoryText(message);
        if (message.Length > 2000)
        {
            return (null, "Сообщение по делу не должно превышать 2000 символов.");
        }

        return (message, null);
    }

    private static IReadOnlyList<ClientCaseConversationEntryDto> MapCaseConversation(int clientCaseId, string? eventHistory, TimeZoneInfo timeZone)
        => SplitCaseHistory(eventHistory)
            .Select((line, index) => MapConversationEntry(clientCaseId, line, index, timeZone))
            .Where(static entry => entry is not null)
            .Select(static entry => entry!)
            .OrderByDescending(item => item.OccurredAt ?? DateTime.MinValue)
            .ThenByDescending(item => item.Id)
            .ToList();

    private static ClientCaseConversationEntryDto? MapConversationEntry(int clientCaseId, string line, int index, TimeZoneInfo timeZone)
    {
        var timelineEntry = MapTimelineEntry(clientCaseId, line, index, timeZone);
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

        var attachments = ExtractConversationAttachments(ref text);
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
            Attachments = attachments,
        };
    }

    private static List<ClientCaseConversationAttachmentDto> ExtractConversationAttachments(ref string text)
    {
        var markerIndex = text.LastIndexOf(CaseConversationAttachmentsMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return [];
        }

        var attachmentSegment = text[(markerIndex + CaseConversationAttachmentsMarker.Length)..].Trim();
        text = text[..markerIndex].Trim();

        return attachmentSegment
            .TrimEnd('.', ' ')
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static item => item.Trim().TrimStart('#', '№').TrimEnd('.', ' '))
            .Select(static item => int.TryParse(item, NumberStyles.Integer, CultureInfo.InvariantCulture, out var documentId)
                ? documentId
                : (int?)null)
            .Where(static documentId => documentId.HasValue)
            .Select(static documentId => new ClientCaseConversationAttachmentDto { DocumentId = documentId!.Value })
            .ToList();
    }

    private static ClientCaseCourtSyncLogDto MapCourtSyncLog(int clientCaseId, string line, int index, TimeZoneInfo timeZone)
    {
        var occurredAt = TryParseCaseHistoryTimestamp(line, timeZone, out var parsed)
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

    private static ClientCaseCourtSyncSearchResultDto MapCourtSyncSearchResult(
        ClientCase clientCase,
        ClientCaseCourtInstance? currentCourtInstance,
        CourtSyncCaseSearchResult item)
    {
        var differences = BuildCourtSyncPreviewDifferences(
            clientCase,
            currentCourtInstance,
            item.CourtName,
            item.CaseNumber,
            item.JudgeName,
            item.SourceUrl);

        return new()
        {
            ProviderKey = item.ProviderKey,
            SourceType = item.SourceType,
            SourceUrl = item.SourceUrl,
            CourtName = item.CourtName,
            CourtCaseNumber = item.CaseNumber,
            JudicialUid = item.JudicialUid,
            Instance = NormalizeCourtInstance(item.Instance),
            InstanceLabel = ResolveCourtInstanceLabel(item.Instance),
            Title = item.Title,
            Summary = item.Summary,
            CanFetch = item.CanFetch,
            Differences = differences,
            RequiresReview = differences.Any(static difference => difference.Kind == "replace"),
        };
    }

    private static IReadOnlyList<ClientCaseCourtSyncDifferenceDto> BuildCourtSyncPreviewDifferences(
        ClientCase clientCase,
        ClientCaseCourtInstance? currentCourtInstance,
        string? courtName,
        string? courtCaseNumber,
        string? judgeName,
        string? sourceUrl)
    {
        var differences = new List<ClientCaseCourtSyncDifferenceDto>(4);
        AddCourtSyncPreviewDifference(
            differences,
            "courtName",
            "Суд",
            NormalizeOptional(currentCourtInstance?.CourtName) ?? clientCase.CourtName,
            courtName);
        AddCourtSyncPreviewDifference(
            differences,
            "courtCaseNumber",
            "Номер дела в суде",
            NormalizeOptional(currentCourtInstance?.CourtCaseNumber) ?? clientCase.CourtCaseNumber,
            courtCaseNumber);
        AddCourtSyncPreviewDifference(
            differences,
            "judgeName",
            "Судья",
            NormalizeOptional(currentCourtInstance?.JudgeName) ?? clientCase.JudgeName,
            judgeName);
        AddCourtSyncPreviewDifference(
            differences,
            "sourceUrl",
            "Ссылка на карточку",
            NormalizeOptional(currentCourtInstance?.CourtUrl) ?? clientCase.CourtUrl,
            sourceUrl);
        return differences;
    }

    private static void AddCourtSyncPreviewDifference(
        ICollection<ClientCaseCourtSyncDifferenceDto> differences,
        string field,
        string label,
        string? currentValue,
        string? sourceValue)
    {
        var normalizedSource = NormalizeOptional(sourceValue);
        if (normalizedSource is null)
        {
            return;
        }

        var normalizedCurrent = NormalizeOptional(currentValue);
        if (string.Equals(normalizedCurrent, normalizedSource, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        differences.Add(new ClientCaseCourtSyncDifferenceDto
        {
            Field = field,
            Label = label,
            CurrentValue = normalizedCurrent ?? string.Empty,
            SourceValue = normalizedSource,
            Kind = normalizedCurrent is null ? "fill" : "replace",
        });
    }

    private CourtSyncJournalProviderInfo ResolveCourtSyncJournalProvider(string? sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return new CourtSyncJournalProviderInfo("unbound", "Без привязки");
        }

        var activeProvider = ResolveCourtSyncProvider(sourceUrl);
        if (activeProvider is not null)
        {
            return new CourtSyncJournalProviderInfo(activeProvider.Key, activeProvider.Label);
        }

        if (Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            var externalCourtSyncEnabled = _featureFlags?.CurrentValue.Legal.ExternalCourtSync ?? false;
            var descriptors = _courtSyncProviderRegistry?.GetProviders()
                ?? CourtSyncProviderRegistry.GetDefaultDescriptors(externalCourtSyncEnabled);
            var descriptor = descriptors.FirstOrDefault(item =>
                item.SupportedHosts.Any(host =>
                    uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase)
                    || uri.Host.EndsWith($".{host}", StringComparison.OrdinalIgnoreCase)));
            if (descriptor is not null)
            {
                return new CourtSyncJournalProviderInfo(descriptor.Key, descriptor.Label);
            }
        }

        return new CourtSyncJournalProviderInfo("other", "Другой источник");
    }

    private static ClientCaseCourtSyncJournalEntryDto MapCourtSyncJournalEntry(
        CourtSyncJournalCaseRow row,
        string line,
        int index,
        TimeZoneInfo timeZone,
        CourtSyncJournalProviderInfo provider,
        DateTime utcNow)
    {
        var log = MapCourtSyncLog(row.ClientCaseId, line, index, timeZone);
        var ageDays = log.OccurredAt.HasValue
            ? Math.Max(0, (int)Math.Floor((utcNow - log.OccurredAt.Value).TotalDays))
            : (int?)null;
        return new ClientCaseCourtSyncJournalEntryDto
        {
            Id = $"{row.ClientCaseId}-{log.Id}",
            LogId = log.Id,
            ClientCaseId = row.ClientCaseId,
            ClientCaseTitle = row.Title,
            CaseNumber = row.CaseNumber,
            OwnerName = row.OwnerName,
            CourtName = row.CourtName,
            CourtCaseNumber = row.CourtCaseNumber,
            CourtUrl = row.CourtUrl,
            ProviderKey = provider.Key,
            ProviderLabel = provider.Label,
            Status = log.Status,
            HasError = string.Equals(log.Status, "external-sync-error", StringComparison.OrdinalIgnoreCase),
            Message = log.Message,
            OccurredAt = log.OccurredAt,
            AgeDays = ageDays,
            ActorName = log.ActorName,
        };
    }

    private static bool CourtSyncJournalEntryMatches(ClientCaseCourtSyncJournalEntryDto entry, string search)
    {
        var values = new[]
        {
            entry.ClientCaseTitle,
            entry.CaseNumber,
            entry.OwnerName,
            entry.CourtName,
            entry.CourtCaseNumber,
            entry.CourtUrl,
            entry.Status,
            entry.Message,
            entry.ActorName ?? string.Empty,
        };

        return values.Any(value => value.Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryParseCaseHistoryTimestamp(string line, TimeZoneInfo timeZone, out DateTime timestamp)
    {
        timestamp = default;
        if (line.Length < 16 ||
            !DateTime.TryParseExact(
                line[..16],
                "dd.MM.yyyy HH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var localTimestamp))
        {
            return false;
        }

        timestamp = CalendarTimeZoneHelper.FromUserLocalToUtc(localTimestamp, timeZone);
        return true;
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
        => FormatCaseResponsible(user?.FullName, user?.Email);

    private static string FormatCaseResponsible(string? fullName, string? email)
        => string.IsNullOrWhiteSpace(fullName)
            ? string.IsNullOrWhiteSpace(email) ? "Ответственный владельца" : email
            : fullName;

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

        if (request.InactivityRemindersEnabled.HasValue)
        {
            targets.Add(request.InactivityRemindersEnabled.Value
                ? "автоконтроль: включен"
                : "автоконтроль: выключен");
        }

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

        if (request.IsUnderControl.HasValue)
        {
            targets.Add(request.IsUnderControl.Value ? "контроль: включен" : "контроль: выключен");
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
        public string? InactivityRemindersDisabledReason { get; set; }
        public bool? IsUnderControl { get; set; }
    }

    public sealed class UpdateClientCaseControlRequest
    {
        public bool IsUnderControl { get; set; }
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

    private sealed class ClientCaseAccessRequestRow
    {
        public int ClientCaseId { get; set; }
        public string CaseNumber { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string? ClientName { get; set; }
        public string? LegalEntityShortName { get; set; }
        public string? LegalEntityFullName { get; set; }
        public int? ResponsibleUserId { get; set; }
        public string? ResponsibleFullName { get; set; }
        public string? ResponsibleEmail { get; set; }
        public DateTime RequestedAt { get; set; }
        public int? AccessRequestedByUserId { get; set; }
        public string AccessRequestedByUserName { get; set; } = string.Empty;
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

    public sealed class AddClientCaseConversationEntryFormRequest
    {
        public string? Text { get; set; }
        public List<IFormFile> Files { get; set; } = [];
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

    public sealed class CreateClientCaseWorkOrderActRequest
    {
        public string? Number { get; set; }
        public DateTime? Date { get; set; }
        public string? Title { get; set; }
        public string? Comment { get; set; }
    }

    public sealed class CreateClientCaseWorkOrderContractRequest
    {
        public string? Number { get; set; }
        public DateTime? Date { get; set; }
        public DateTime? StartDate { get; set; }
        public DateTime? ValidUntil { get; set; }
        public bool? IsOpenEnded { get; set; }
        public string? Title { get; set; }
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

    public sealed class ClientCaseWorkOrderActDto
    {
        public int ActId { get; set; }
        public string Number { get; set; } = string.Empty;
        public DateTime Date { get; set; }
        public decimal Amount { get; set; }
        public int? DocumentId { get; set; }
        public int LineItemCount { get; set; }
        public ClientCaseWorkOrderDto WorkOrder { get; set; } = new();
    }

    public sealed class ClientCaseWorkOrderContractDto
    {
        public int ContractId { get; set; }
        public string Number { get; set; } = string.Empty;
        public DateTime Date { get; set; }
        public DateTime StartDate { get; set; }
        public decimal? Amount { get; set; }
        public int? DocumentId { get; set; }
        public int? ClientId { get; set; }
        public int? LegalEntityId { get; set; }
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
        public string SourceUrl { get; set; } = string.Empty;
        public DateTime? SourceCheckedAt { get; set; }
        public string SourceConfidence { get; set; } = string.Empty;
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

    public sealed class ClientCaseCourtInstanceDto
    {
        public int Id { get; set; }
        public int ClientCaseId { get; set; }
        public string Instance { get; set; } = string.Empty;
        public string InstanceLabel { get; set; } = string.Empty;
        public int? CourtId { get; set; }
        public string CourtName { get; set; } = string.Empty;
        public string CourtCaseNumber { get; set; } = string.Empty;
        public string CourtUrl { get; set; } = string.Empty;
        public string JudgeName { get; set; } = string.Empty;
        public string Courtroom { get; set; } = string.Empty;
        public string SecretaryName { get; set; } = string.Empty;
        public string Notes { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public int? CreatedByUserId { get; set; }
        public string CreatedByUserName { get; set; } = string.Empty;
        public DateTime? UpdatedAt { get; set; }
        public int? UpdatedByUserId { get; set; }
        public string UpdatedByUserName { get; set; } = string.Empty;
        public IReadOnlyList<ClientCaseCourtComplaintDto> Complaints { get; set; } = [];
    }

    public sealed class UpsertClientCaseCourtInstanceRequest
    {
        public int? CourtId { get; set; }
        public string? CourtName { get; set; }
        public string? CourtCaseNumber { get; set; }
        public string? CourtUrl { get; set; }
        public string? JudgeName { get; set; }
        public string? Courtroom { get; set; }
        public string? SecretaryName { get; set; }
        public string? Notes { get; set; }
    }

    public sealed class ClientCaseCourtComplaintDto
    {
        public int Id { get; set; }
        public int ClientCaseId { get; set; }
        public int CourtInstanceId { get; set; }
        public string ComplaintType { get; set; } = string.Empty;
        public string ComplaintTypeLabel { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string TargetInstance { get; set; } = string.Empty;
        public string TargetInstanceLabel { get; set; } = string.Empty;
        public DateTime? FiledAt { get; set; }
        public string Status { get; set; } = string.Empty;
        public string StatusLabel { get; set; } = string.Empty;
        public int? ClientCaseDocumentId { get; set; }
        public string Notes { get; set; } = string.Empty;
        public bool IsArchived { get; set; }
        public DateTime CreatedAt { get; set; }
        public int? CreatedByUserId { get; set; }
        public string CreatedByUserName { get; set; } = string.Empty;
        public DateTime? UpdatedAt { get; set; }
        public int? UpdatedByUserId { get; set; }
        public string UpdatedByUserName { get; set; } = string.Empty;
    }

    public sealed class UpsertClientCaseCourtComplaintRequest
    {
        public string? ComplaintType { get; set; }
        public string? Title { get; set; }
        public string? TargetInstance { get; set; }
        public DateTime? FiledAt { get; set; }
        public string? Status { get; set; }
        public int? ClientCaseDocumentId { get; set; }
        public string? Notes { get; set; }
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
        public string SourceUrl { get; set; } = string.Empty;
        public DateTime? SourceCheckedAt { get; set; }
        public string SourceConfidence { get; set; } = string.Empty;
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
        public string SourceType { get; set; } = string.Empty;
        public string SourceUrl { get; set; } = string.Empty;
        public DateTime? SourceCheckedAt { get; set; }
        public string SourceConfidence { get; set; } = string.Empty;
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
        public DateTime? StartAt { get; set; }
        public DateTime? RawDeadlineAt { get; set; }
        public int SkippedNonWorkingDays { get; set; }
        public int RollForwardDays { get; set; }
        public string Explanation { get; set; } = string.Empty;
        public ProceduralDeadlineCalendarSettingsDto? CalendarOverrides { get; set; }
    }

    public sealed class ProceduralDeadlineCalendarSettingsDto
    {
        public List<ProceduralDeadlineCalendarDateDto> WorkingDates { get; set; } = [];
        public List<ProceduralDeadlineCalendarDateDto> NonWorkingDates { get; set; } = [];
        public DateTime? UpdatedAt { get; set; }
    }

    public sealed class ProceduralDeadlineCalendarDateDto
    {
        public string Date { get; set; } = string.Empty;
        public string? Note { get; set; }
    }

    public sealed class UpsertProceduralDeadlineCalendarSettingsRequest
    {
        public List<ProceduralDeadlineCalendarDateRequest> WorkingDates { get; set; } = [];
        public List<ProceduralDeadlineCalendarDateRequest> NonWorkingDates { get; set; } = [];
    }

    public sealed class ProceduralDeadlineCalendarDateRequest
    {
        public string Date { get; set; } = string.Empty;
        public string? Note { get; set; }
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
        public DateTime? LastViewedByResponsibleAt { get; set; }
        public int? LastViewedByResponsibleUserId { get; set; }
        public string LastViewedByResponsibleUserName { get; set; } = string.Empty;
        public DateTime? ActivityConfirmedAt { get; set; }
        public int? ActivityConfirmedByUserId { get; set; }
        public string ActivityConfirmedByUserName { get; set; } = string.Empty;
        public bool InactivityRemindersEnabled { get; set; }
        public string InactivityRemindersDisabledReason { get; set; } = string.Empty;
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
        public List<ClientCaseConversationAttachmentDto> Attachments { get; set; } = [];
    }

    public sealed class ClientCaseConversationAttachmentDto
    {
        public int DocumentId { get; set; }
        public string FileName { get; set; } = string.Empty;
        public string ContentType { get; set; } = "application/octet-stream";
        public long SizeBytes { get; set; }
        public string UploadedByName { get; set; } = string.Empty;
        public DateTime? UploadedAt { get; set; }
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
        public DateTime? LastViewedByResponsibleAt { get; set; }
        public int? LastViewedByResponsibleUserId { get; set; }
        public string LastViewedByResponsibleUserName { get; set; } = string.Empty;
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

    public sealed class ClientCaseCourtSyncRunRequest
    {
        public bool ManualOnly { get; set; }
        public string? Comment { get; set; }
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
        public int CreatedCourtParties { get; set; }
        public int CreatedCourtActDocuments { get; set; }
        public int CreatedProceduralDeadlines { get; set; }
        public IReadOnlyList<string> NewItems { get; set; } = [];
        public IReadOnlyList<string> ChangedItems { get; set; } = [];
        public IReadOnlyList<string> RemovedItems { get; set; } = [];
        public IReadOnlyList<string> ConflictItems { get; set; } = [];
        public DateTime? NextHearingAt { get; set; }
    }

    public sealed class ClientCaseCourtSyncSearchRequest
    {
        public string? SourceUrl { get; set; }
        public string? CourtCaseNumber { get; set; }
        public string? JudicialUid { get; set; }
        public string? CourtName { get; set; }
        public string? PartyName { get; set; }
        public string? Instance { get; set; }
    }

    public sealed class ClientCaseCourtSyncSearchResultDto
    {
        public string ProviderKey { get; set; } = string.Empty;
        public string SourceType { get; set; } = string.Empty;
        public string SourceUrl { get; set; } = string.Empty;
        public string CourtName { get; set; } = string.Empty;
        public string CourtCaseNumber { get; set; } = string.Empty;
        public string JudicialUid { get; set; } = string.Empty;
        public string Instance { get; set; } = string.Empty;
        public string InstanceLabel { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public bool CanFetch { get; set; }
        public bool RequiresReview { get; set; }
        public IReadOnlyList<ClientCaseCourtSyncDifferenceDto> Differences { get; set; } = [];
    }

    public sealed class ClientCaseCourtSyncDifferenceDto
    {
        public string Field { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string CurrentValue { get; set; } = string.Empty;
        public string SourceValue { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
    }

    public sealed class ClientCaseCourtSyncBindRequest
    {
        public string? Instance { get; set; }
        public string? ProviderKey { get; set; }
        public string? SourceType { get; set; }
        public string? SourceUrl { get; set; }
        public string? CourtName { get; set; }
        public string? CourtCaseNumber { get; set; }
        public string? JudicialUid { get; set; }
        public string? JudgeName { get; set; }
        public bool ConfirmDifferences { get; set; }
    }

    public sealed class ClientCaseCourtSyncBindingDto
    {
        public int ClientCaseId { get; set; }
        public string Instance { get; set; } = string.Empty;
        public string ProviderKey { get; set; } = string.Empty;
        public string SourceType { get; set; } = string.Empty;
        public string CourtName { get; set; } = string.Empty;
        public string CourtCaseNumber { get; set; } = string.Empty;
        public string CourtUrl { get; set; } = string.Empty;
        public string JudicialUid { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public bool SyncedFromExternalSource { get; set; }
        public IReadOnlyList<string> UpdatedFields { get; set; } = [];
        public int CreatedCourtEvents { get; set; }
        public int UpdatedCourtEvents { get; set; }
        public int CreatedCourtParties { get; set; }
        public int CreatedCourtActDocuments { get; set; }
        public int CreatedProceduralDeadlines { get; set; }
        public IReadOnlyList<string> NewItems { get; set; } = [];
        public IReadOnlyList<string> ChangedItems { get; set; } = [];
        public IReadOnlyList<string> RemovedItems { get; set; } = [];
        public IReadOnlyList<string> ConflictItems { get; set; } = [];
        public string SyncError { get; set; } = string.Empty;
        public ClientCaseCourtInstanceDto CourtInstance { get; set; } = new();
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

    public sealed class ClientCaseCourtSyncJournalEntryDto
    {
        public string Id { get; set; } = string.Empty;
        public int LogId { get; set; }
        public int ClientCaseId { get; set; }
        public string ClientCaseTitle { get; set; } = string.Empty;
        public string CaseNumber { get; set; } = string.Empty;
        public string OwnerName { get; set; } = string.Empty;
        public string CourtName { get; set; } = string.Empty;
        public string CourtCaseNumber { get; set; } = string.Empty;
        public string CourtUrl { get; set; } = string.Empty;
        public string ProviderKey { get; set; } = string.Empty;
        public string ProviderLabel { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public bool HasError { get; set; }
        public string Message { get; set; } = string.Empty;
        public DateTime? OccurredAt { get; set; }
        public int? AgeDays { get; set; }
        public string? ActorName { get; set; }
    }

    public sealed class ClientCaseCourtSyncSettingsDto
    {
        public bool IsEnabled { get; set; }
        public bool FeatureFlagEnabled { get; set; }
        public int IntervalMinutes { get; set; }
        public int BatchSize { get; set; }
        public int MinIntervalMinutes { get; set; }
        public int MaxIntervalMinutes { get; set; }
        public int MinBatchSize { get; set; }
        public int MaxBatchSize { get; set; }
    }

    public sealed class UpdateClientCaseCourtSyncSettingsRequest
    {
        public bool IsEnabled { get; set; }
        public int? IntervalMinutes { get; set; }
        public int? BatchSize { get; set; }
    }

    public sealed class ClientCaseCourtSyncProviderDto
    {
        public string Key { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string SourceType { get; set; } = string.Empty;
        public int SchemaVersion { get; set; }
        public IReadOnlyList<string> Capabilities { get; set; } = [];
        public string Status { get; set; } = string.Empty;
        public bool IsAvailable { get; set; }
        public bool FeatureFlagEnabled { get; set; }
        public bool SupportsManualRun { get; set; }
        public bool SupportsBackgroundSync { get; set; }
        public IReadOnlyList<string> SupportedHosts { get; set; } = [];
        public string Message { get; set; } = string.Empty;
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

    private sealed class ProductionCalendarSettingsStorage
    {
        public List<ProductionCalendarDateStorage> WorkingDates { get; set; } = [];
        public List<ProductionCalendarDateStorage> NonWorkingDates { get; set; } = [];
        public DateTime? UpdatedAt { get; set; }
    }

    private sealed class ProductionCalendarDateStorage
    {
        public string Date { get; set; } = string.Empty;
        public string? Note { get; set; }
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

    private readonly record struct CourtInstanceValidationResult(
        string? Error,
        string CourtName = "",
        string CourtCaseNumber = "",
        string CourtUrl = "",
        string JudgeName = "",
        string Courtroom = "",
        string SecretaryName = "",
        string Notes = "");

    private readonly record struct CourtComplaintValidationResult(
        string? Error,
        string ComplaintType = "appealComplaint",
        string Title = "",
        string TargetInstance = "appeal",
        DateTime? FiledAt = null,
        string Status = "draft",
        int? ClientCaseDocumentId = null,
        string Notes = "");

    private readonly record struct CourtSyncApplyResult(
        IReadOnlyList<string> ChangedFields,
        int CreatedCourtEvents,
        int UpdatedCourtEvents,
        int CreatedCourtParties,
        int CreatedCourtActDocuments,
        int CreatedProceduralDeadlines,
        IReadOnlyList<string> NewItems,
        IReadOnlyList<string> ChangedItems,
        IReadOnlyList<string> RemovedItems,
        IReadOnlyList<string> ConflictItems,
        bool RequiresDeadlineTaskSync)
    {
        public bool HasChanges =>
            NewItems.Count > 0
            || ChangedItems.Count > 0
            || RemovedItems.Count > 0
            || ConflictItems.Count > 0;
    }

    private readonly record struct CourtSyncJournalCaseRow(
        int ClientCaseId,
        string Title,
        string CaseNumber,
        string OwnerName,
        string CourtName,
        string CourtCaseNumber,
        string CourtUrl,
        string EventHistory);

    private readonly record struct CourtSyncJournalProviderInfo(
        string Key,
        string Label);

    private readonly record struct SudrfCourtActDeadlineSpec(
        string DeadlineType,
        string Title,
        string Description,
        DateTime DueAt);

    private readonly record struct SudrfCourtPartyCandidate(
        string DisplayName,
        string Side,
        string ProcessRole,
        string RepresentativeName,
        string Notes);

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
