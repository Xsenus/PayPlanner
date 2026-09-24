using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Documents;
using PayPlanner.Api.Services.Invoices;
using PayPlanner.Api.Services.Notifications;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/tasks")]
[Authorize]
public class TasksController : ControllerBase
{
    private const long MaxTaskFileBytes = 25L * 1024L * 1024L;
    private const string InternalTaskObjectName = "Внутренняя задача";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "new", "inProgress", "waiting", "onApproval", "approved", "revision", "overdueApproval", "done", "cancelled"
    };
    private static readonly HashSet<string> AllowedPriorities = new(StringComparer.OrdinalIgnoreCase)
    {
        "low", "normal", "high", "urgent"
    };
    private static readonly HashSet<string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "task", "budget", "contact", "document", "reminder", "control", "courtHearing", "meeting"
    };
    private static readonly HashSet<string> AllowedReminders = new(StringComparer.OrdinalIgnoreCase)
    {
        "atDue", "before15m", "before1h", "before1d", "before3d", "before7d", "dailyOverdue"
    };
    private static readonly HashSet<string> AllowedChannels = new(StringComparer.OrdinalIgnoreCase)
    {
        "inApp", "email", "telegram"
    };
    private static readonly HashSet<string> AllowedParticipantRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "assignee", "observer", "approver"
    };
    private static readonly HashSet<string> AllowedApprovalStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "pending", "approved", "revisionRequested"
    };
    private static readonly HashSet<string> AllowedApprovalDecisions = new(StringComparer.OrdinalIgnoreCase)
    {
        "approved", "revisionRequested"
    };
    private static readonly HashSet<string> AllowedTemplateBindingTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "none", "caseStage", "caseType"
    };
    private static readonly string[] ActiveTaskStatuses =
    [
        "new", "inProgress", "waiting", "onApproval", "approved", "revision", "overdueApproval"
    ];
    private static readonly HashSet<string> AllowedAutomationPreviewCategories = new(StringComparer.Ordinal)
    {
        "candidate", "criticalCandidate", "activeTask", "unassigned", "recentActivity", "excludedStatus", "disabled", "vacation"
    };
    private const string RestrictExecutorEditingSettingKey = "tasks.restrictExecutorEditing";
    private const string AllowExecutorDeadlineChangeRequestsSettingKey = "tasks.allowExecutorDeadlineChangeRequests";
    private const string ExecutorCompletionAllowedUserIdsSettingKey = "tasks.executorCompletionAllowedUserIds";
    private const string RequireCompletionResultSettingKey = "tasks.requireCompletionResult";
    private const string DailyOverdueReminderEnabledSettingKey = "tasks.dailyOverdueReminderEnabled";
    private const string BirthdayTaskAutomationEnabledSettingKey = "tasks.automation.birthdays.enabled";
    private const string EmployeeBirthdayTaskAutomationEnabledSettingKey = "tasks.automation.birthdays.employees.enabled";
    private const string BirthdayTaskLeadDaysSettingKey = "tasks.automation.birthdays.leadDays";
    private const string BirthdayClientUseSelectedAssigneeSettingKey = "tasks.automation.birthdays.clients.useSelectedAssignee";
    private const string BirthdayTaskAssigneeUserIdSettingKey = "tasks.automation.birthdays.assigneeUserId";
    private const string AllowAdminCompleteAnyTaskSettingKey = "tasks.allowAdminCompleteAnyTask";
    private const string FinancialDocumentsResponsibleUserSettingKey = "tasks.financialDocumentsResponsibleUserId";
    private const string ApprovalDueDaysSettingKey = "tasks.approvalDueDays";
    private const int DefaultApprovalDueDays = 5;
    private const string TaskCardWindowModeSettingKey = "tasks.taskCardWindowMode";
    private const string TaskCardWindowModeLegacy = "legacy";
    private const string TaskCardWindowModeModern = "modern";
    private const string ApprovalQuickActionModeSettingKey = "tasks.approvalQuickActionMode";
    private const string ApprovalQuickActionModeCompletionButton = "completionButton";
    private const string ApprovalQuickActionModeSeparateButton = "separateButton";
    private const string TaskStickerStylesSettingKey = "tasks.stickerStyles";
    private const string CaseInactivityAutomationEnabledSettingKey = "tasks.automation.caseControl.enabled";
    private const string CaseInactivityAutomationDaysSettingKey = "tasks.automation.caseControl.inactiveDays";
    private const string CaseInactivityAutomationLimitSettingKey = "tasks.automation.caseControl.limit";
    private const string CaseInactivityAutomationDefaultAssigneeSettingKey = "tasks.automation.caseControl.defaultAssigneeUserId";
    private const string CaseInactivityAutomationEscalationDaysSettingKey = "tasks.automation.caseControl.escalationDays";
    private const string CaseInactivityAutomationEscalationUserSettingKey = "tasks.automation.caseControl.escalationUserId";
    private const string CaseInactivityAutomationRunLocalTimeSettingKey = "tasks.automation.caseControl.runLocalTime";
    private const string CaseInactivityAutomationScheduleTimeZoneSettingKey = "tasks.automation.caseControl.scheduleTimeZone";
    private const string CaseInactivityAutomationTag = CaseInactivityAutomationQuery.AutomationTag;
    private const string ContractorPaymentMarker = "[PayPlanner:TaskContractorPayment]";
    private const string CalendarSyncSettingsKey = "calendarSync.settings";
    private const string ProfileTimeZoneSettingKey = "profile.timeZoneId";
    private static readonly IReadOnlyDictionary<string, TaskStickerStyle> DefaultTaskStickerStyles =
        new Dictionary<string, TaskStickerStyle>(StringComparer.Ordinal)
        {
            ["priority.low"] = new() { Label = "Низкий", ShortLabel = "Низкий", BackgroundColor = "#f8fafc", TextColor = "#475569", BorderColor = "#e2e8f0" },
            ["priority.normal"] = new() { Label = "Обычный", ShortLabel = "Обычный", BackgroundColor = "#eff6ff", TextColor = "#1d4ed8", BorderColor = "#bfdbfe" },
            ["priority.high"] = new() { Label = "Высокий", ShortLabel = "Высокий", BackgroundColor = "#fffbeb", TextColor = "#b45309", BorderColor = "#fde68a" },
            ["priority.urgent"] = new() { Label = "Срочно", ShortLabel = "Срочно", BackgroundColor = "#fff1f2", TextColor = "#be123c", BorderColor = "#fecdd3" },
            ["billing.unbilled"] = new() { Label = "ПЛАТНАЯ · НЕ ВЫСТАВЛЕН СЧЁТ", ShortLabel = "БЕЗ СЧЁТА", BackgroundColor = "#ffe4e6", TextColor = "#9f1239", BorderColor = "#fda4af" },
            ["billing.invoiced"] = new() { Label = "В СЧЁТЕ", ShortLabel = "В СЧЁТЕ", BackgroundColor = "#d1fae5", TextColor = "#065f46", BorderColor = "#6ee7b7" },
            ["billing.fixed"] = new() { Label = "КЛИЕНТСКАЯ · ФИКСИРОВАННЫЙ ДОГОВОР", ShortLabel = "ФИКС. ДОГОВОР", BackgroundColor = "#ede9fe", TextColor = "#5b21b6", BorderColor = "#c4b5fd" },
            ["billing.unpaid"] = new() { Label = "НЕОПЛАЧИВАЕМАЯ · НЕ ДЛЯ СЧЁТА", ShortLabel = "НЕ ДЛЯ СЧЁТА", BackgroundColor = "#f1f5f9", TextColor = "#334155", BorderColor = "#cbd5e1" },
            ["billing.internal"] = new() { Label = "ВНУТРЕННЯЯ · НЕ ДЛЯ СЧЁТА", ShortLabel = "НЕ ДЛЯ СЧЁТА", BackgroundColor = "#f1f5f9", TextColor = "#334155", BorderColor = "#cbd5e1" },
        };

    private readonly PaymentContext _db;
    private readonly INotificationService _notificationService;
    private readonly ICaseDocumentStorage _caseDocumentStorage;
    private readonly IInvoiceDocumentPdfService? _pdfService;
    private readonly IExternalCalendarSyncCoordinator? _externalCalendarSyncCoordinator;
    private readonly IExternalCalendarTaskSyncOutbox? _externalCalendarTaskSyncOutbox;
    private readonly CaseInactivityAutomationMutationGate _caseInactivityAutomationMutationGate;
    private readonly ISystemJobRunStateService? _systemJobRunState;
    private readonly IRealtimeNotificationPublisher _realtimePublisher;
    private readonly string _taskFilesRoot;

    public TasksController(
        PaymentContext db,
        INotificationService notificationService,
        IWebHostEnvironment? environment = null,
        ICaseDocumentStorage? caseDocumentStorage = null,
        IInvoiceDocumentPdfService? pdfService = null,
        IExternalCalendarSyncCoordinator? externalCalendarSyncCoordinator = null,
        IExternalCalendarTaskSyncOutbox? externalCalendarTaskSyncOutbox = null,
        IConfiguration? configuration = null,
        CaseInactivityAutomationMutationGate? caseInactivityAutomationMutationGate = null,
        ISystemJobRunStateService? systemJobRunState = null,
        IRealtimeNotificationPublisher? realtimePublisher = null)
    {
        _db = db;
        _notificationService = notificationService;
        _caseDocumentStorage = caseDocumentStorage ?? new LocalCaseDocumentStorage(environment, configuration);
        _pdfService = pdfService;
        _externalCalendarSyncCoordinator = externalCalendarSyncCoordinator;
        _externalCalendarTaskSyncOutbox = externalCalendarTaskSyncOutbox;
        _caseInactivityAutomationMutationGate = caseInactivityAutomationMutationGate
            ?? new CaseInactivityAutomationMutationGate();
        _systemJobRunState = systemJobRunState;
        _realtimePublisher = realtimePublisher ?? NoOpRealtimeNotificationPublisher.Instance;
        _taskFilesRoot = Path.Combine(
            ApplicationFileStoragePaths.ResolveRoot(environment, configuration, AppContext.BaseDirectory),
            "task-files");
    }

    [HttpGet]
    public async Task<ActionResult<PagedResponse<TaskJournalItemDto>>> GetTasks(
        [FromQuery] string? search,
        [FromQuery] string? status,
        [FromQuery] string? type,
        [FromQuery] int? assigneeUserId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int? clientCaseId = null,
        [FromQuery] int? clientId = null,
        [FromQuery] int? legalEntityId = null,
        [FromQuery] string? priority = null,
        [FromQuery] bool? underControl = null,
        [FromQuery] int? authorUserId = null,
        [FromQuery] string? participantRole = null,
        [FromQuery] bool approvalScope = false,
        [FromQuery] string? metric = null,
        [FromQuery] DateTime? todayFrom = null,
        [FromQuery] DateTime? todayTo = null,
        [FromQuery] bool calendarView = false,
        [FromQuery] bool includeDeadlineChangeRequests = false,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? sortBy = null,
        [FromQuery] string? sortDir = null,
        [FromQuery] string? billingStatus = null,
        [FromQuery] bool untriaged = false,
        CancellationToken ct = default)
    {
        var access = await GetTaskAccessAsync(ct, calendarView ? RolePermissionCatalog.Calendar : RolePermissionCatalog.Tasks);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin && access.Permission?.CanView != true)
        {
            return Forbid();
        }

        var query = BuildTaskJournalListQuery(
            access,
            search,
            status,
            type,
            assigneeUserId,
            from,
            to,
            clientCaseId,
            clientId,
            legalEntityId,
            priority,
            underControl,
            authorUserId,
            participantRole,
            approvalScope,
            metric,
            todayFrom,
            todayTo,
            billingStatus);

        if (untriaged)
        {
            query = query.Where(task =>
                !task.ClientId.HasValue &&
                !task.LegalEntityId.HasValue &&
                !task.AssigneeUserId.HasValue &&
                task.AssigneeName == "");
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var total = await query.CountAsync(ct);
        var pageQuery = ApplyTaskJournalSort(query, sortBy, sortDir)
            .Skip((page - 1) * pageSize)
            .Take(pageSize);
        var items = await ProjectTaskReadItems(pageQuery)
            .ToListAsync(ct);
        var activeDeadlineChangeRequests = includeDeadlineChangeRequests
            ? await LoadActiveDeadlineChangeRequestsAsync(items.Select(item => item.Id).ToList(), ct)
            : new Dictionary<int, IReadOnlyList<TaskJournalDeadlineChangeRequest>>();

        return Ok(new PagedResponse<TaskJournalItemDto>
        {
            Items = items
                .Select(item => Map(
                    item,
                    activeDeadlineChangeRequests: activeDeadlineChangeRequests.GetValueOrDefault(item.Id)))
                .ToList(),
            Total = total,
            Page = page,
            PageSize = pageSize,
        });
    }

    [HttpGet("notifications")]
    public async Task<ActionResult<IReadOnlyList<TaskJournalNotificationDto>>> GetNotifications(CancellationToken ct)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin && access.Permission?.CanView != true)
        {
            return Forbid();
        }

        var now = DateTime.UtcNow;
        var soon = now.AddHours(1);
        var dueTasks = await BuildVisibleTaskBaseQuery(access)
            .AsNoTracking()
            .Where(task => task.Status != "done" && task.Status != "cancelled" && task.DueAt <= soon)
            .OrderBy(task => task.DueAt)
            .Take(20)
            .ToListAsync(ct);

        var completedSince = now.AddDays(-7);
        var completedTasks = await BuildVisibleTaskBaseQuery(access)
            .AsNoTracking()
            .Where(task =>
                task.Status == "done"
                && task.AuthorUserId == access.UserId
                && (task.UpdatedAt ?? task.CreatedAt) >= completedSince)
            .OrderByDescending(task => task.UpdatedAt ?? task.CreatedAt)
            .Take(20)
            .ToListAsync(ct);

        var dueNotifications = dueTasks.Select(task =>
        {
            var overdue = task.DueAt < now;
            return new TaskJournalNotificationDto
            {
                Id = $"{task.Id}-{(overdue ? "overdue" : "soon")}",
                TaskId = task.Id,
                Title = overdue ? "Просрочена задача" : "Скоро срок",
                Description = $"{task.ObjectName}: {task.Title}",
                Tone = overdue ? "rose" : "amber",
                DueAt = ToIso(task.DueAt),
            };
        });

        var completedNotifications = completedTasks.Select(task =>
        {
            var completedAt = task.UpdatedAt ?? task.CreatedAt;
            return new TaskJournalNotificationDto
            {
                Id = $"{task.Id}-completed-{completedAt:yyyyMMddHHmmss}",
                TaskId = task.Id,
                Title = "Задача выполнена",
                Description = string.IsNullOrWhiteSpace(task.AssigneeName)
                    ? task.Title
                    : $"{task.AssigneeName}: {task.Title}",
                Tone = "blue",
                DueAt = ToIso(completedAt),
            };
        });

        return Ok(dueNotifications.Concat(completedNotifications).Take(20).ToList());
    }

    [HttpGet("overdue-summary")]
    public async Task<ActionResult<TaskJournalOverdueSummaryDto>> GetOverdueSummary(CancellationToken ct)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin && access.Permission?.CanView != true)
        {
            return Forbid();
        }

        var now = DateTime.UtcNow;
        var count = await BuildVisibleTasksQuery(access)
            .AsNoTracking()
            .CountAsync(task =>
                task.AssigneeUserId == access.UserId &&
                ActiveTaskStatuses.Contains(task.Status) &&
                task.DueAt < now,
                ct);

        return Ok(new TaskJournalOverdueSummaryDto { Count = count });
    }

    [HttpGet("assignees")]
    public async Task<ActionResult<IReadOnlyList<TaskJournalAssigneeDto>>> GetAssignees([FromQuery] bool calendarView = false, CancellationToken ct = default)
    {
        var access = await GetTaskAccessAsync(ct, calendarView ? RolePermissionCatalog.Calendar : RolePermissionCatalog.Tasks);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin && access.Permission?.CanView != true)
        {
            return Forbid();
        }

        var canManageTaskAssignments = !calendarView
            && (access.IsAdmin
                || access.Permission?.CanCreate == true
                || access.Permission?.CanEdit == true);
        var visibleUserIds = access.Visibility.VisibleUserIds.ToArray();
        var users = await _db.Users
            .AsNoTracking()
            .Where(user => user.IsActive && user.IsApproved)
            .Where(user => canManageTaskAssignments || access.Visibility.SeesAll || visibleUserIds.Contains(user.Id))
            .OrderBy(user => user.FullName)
            .ThenBy(user => user.Email)
            .Select(user => new TaskJournalAssigneeDto
            {
                Id = user.Id,
                Name = user.FullName == "" ? user.Email : user.FullName,
                Email = user.Email,
                IsExternalContractor = user.IsExternalContractor,
                DepartmentId = user.DepartmentId,
                Department = user.Department,
            })
            .ToListAsync(ct);

        var userIds = users.Select(user => user.Id).ToArray();
        var timeZones = await _db.UserSettings
            .AsNoTracking()
            .Where(setting => userIds.Contains(setting.UserId) && setting.Key == ProfileTimeZoneSettingKey)
            .ToDictionaryAsync(setting => setting.UserId, setting => setting.Value, ct);

        foreach (var user in users)
        {
            if (timeZones.TryGetValue(user.Id, out var timeZoneId) && !string.IsNullOrWhiteSpace(timeZoneId))
            {
                user.TimeZoneId = timeZoneId;
            }
        }

        return Ok(users);
    }

    [HttpGet("settings")]
    public async Task<ActionResult<TaskJournalSettingsDto>> GetSettings(CancellationToken ct)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin && access.Permission?.CanView != true)
        {
            return Forbid();
        }

        return Ok(await GetTaskJournalSettingsAsync(ct));
    }

    [HttpPut("settings")]
    public async Task<ActionResult<TaskJournalSettingsDto>> UpdateSettings(
        [FromBody] TaskJournalSettingsRequest request,
        CancellationToken ct)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin)
        {
            return Forbid();
        }

        var now = DateTime.UtcNow;
        var inactivityDays = Math.Clamp(request.CaseInactivityDays, 1, 365);
        var inactivityLimit = Math.Clamp(request.CaseInactivityLimit, 1, 100);
        var escalationDays = Math.Clamp(request.CaseInactivityEscalationDays, inactivityDays, 365);
        var runLocalTime = TimeOnly.TryParseExact(
            request.CaseInactivityRunLocalTime,
            "HH:mm",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsedRunLocalTime)
            ? parsedRunLocalTime.ToString("HH:mm", CultureInfo.InvariantCulture)
            : "09:00";
        var scheduleTimeZone = CalendarTimeZoneHelper.ResolveIanaTimeZoneId(
            CalendarTimeZoneHelper.ResolveTimeZone(request.CaseInactivityScheduleTimeZone));
        var requestedDefaultAssigneeUserId = request.CaseInactivityDefaultAssigneeUserId.GetValueOrDefault();
        var defaultAssigneeUserId = requestedDefaultAssigneeUserId > 0
            ? requestedDefaultAssigneeUserId.ToString()
            : "0";
        var requestedEscalationUserId = request.CaseInactivityEscalationUserId.GetValueOrDefault();
        var escalationUserId = requestedEscalationUserId > 0
            ? requestedEscalationUserId.ToString()
            : "0";
        var taskCardWindowMode = NormalizeTaskCardWindowMode(request.TaskCardWindowMode);
        var approvalQuickActionMode = NormalizeApprovalQuickActionMode(request.ApprovalQuickActionMode);
        var approvalDueDays = Math.Clamp(request.ApprovalDueDays, 1, 365);
        var financialDocumentsResponsibleUserId = request.FinancialDocumentsResponsibleUserId.GetValueOrDefault();
        var birthdayAssigneeUserId = request.BirthdayTaskAssigneeUserId.GetValueOrDefault();
        var birthdaySelectedAssigneeRequired = request.EmployeeBirthdayTaskAutomationEnabled ||
            (request.BirthdayTaskAutomationEnabled && request.BirthdayClientUseSelectedAssignee);
        if (birthdaySelectedAssigneeRequired && birthdayAssigneeUserId <= 0)
        {
            return BadRequest(new { message = "Выберите ответственного за поздравления." });
        }
        if (birthdaySelectedAssigneeRequired && !await _db.Users.AsNoTracking().AnyAsync(user =>
                user.Id == birthdayAssigneeUserId && user.IsActive && user.IsApproved, ct))
        {
            return BadRequest(new { message = "Ответственный за поздравления недоступен." });
        }
        if (financialDocumentsResponsibleUserId > 0)
        {
            var responsibleUserExists = await _db.Users.AsNoTracking().AnyAsync(user =>
                user.Id == financialDocumentsResponsibleUserId && user.IsActive && user.IsApproved, ct);
            if (!responsibleUserExists)
            {
                return BadRequest(new { message = "Ответственный за финансовые документы недоступен." });
            }
        }
        var executorCompletionAllowedUserIds = request.ExecutorCompletionAllowedUserIds
            .Where(id => id > 0)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        if (executorCompletionAllowedUserIds.Length > 0)
        {
            var activeUserIds = await _db.Users
                .AsNoTracking()
                .Where(user => executorCompletionAllowedUserIds.Contains(user.Id) && user.IsActive && user.IsApproved)
                .Select(user => user.Id)
                .ToListAsync(ct);
            if (activeUserIds.Count != executorCompletionAllowedUserIds.Length)
            {
                return BadRequest(new { message = "Список сотрудников с правом завершения содержит недоступного пользователя." });
            }
        }

        Dictionary<string, TaskStickerStyle>? normalizedStickerStyles = null;
        if (request.StickerStyles is not null)
        {
            if (!TryNormalizeTaskStickerStyles(request.StickerStyles, rejectUnknownKeys: true, out normalizedStickerStyles, out var stickerStylesError))
            {
                return BadRequest(new { message = stickerStylesError });
            }
        }

        await UpsertSystemSettingAsync(RestrictExecutorEditingSettingKey, request.RestrictExecutorEditing ? "true" : "false", now, ct);
        await UpsertSystemSettingAsync(AllowExecutorDeadlineChangeRequestsSettingKey, request.AllowExecutorDeadlineChangeRequests ? "true" : "false", now, ct);
        await UpsertSystemSettingAsync(ExecutorCompletionAllowedUserIdsSettingKey, JsonSerializer.Serialize(executorCompletionAllowedUserIds, JsonOptions), now, ct);
        await UpsertSystemSettingAsync(RequireCompletionResultSettingKey, request.RequireCompletionResult ? "true" : "false", now, ct);
        await UpsertSystemSettingAsync(DailyOverdueReminderEnabledSettingKey, request.DailyOverdueReminderEnabled ? "true" : "false", now, ct);
        await UpsertSystemSettingAsync(BirthdayTaskAutomationEnabledSettingKey, request.BirthdayTaskAutomationEnabled ? "true" : "false", now, ct);
        await UpsertSystemSettingAsync(EmployeeBirthdayTaskAutomationEnabledSettingKey, request.EmployeeBirthdayTaskAutomationEnabled ? "true" : "false", now, ct);
        await UpsertSystemSettingAsync(BirthdayTaskLeadDaysSettingKey, Math.Clamp(request.BirthdayTaskLeadDays, 0, 60).ToString(), now, ct);
        await UpsertSystemSettingAsync(BirthdayClientUseSelectedAssigneeSettingKey, request.BirthdayClientUseSelectedAssignee ? "true" : "false", now, ct);
        await UpsertSystemSettingAsync(BirthdayTaskAssigneeUserIdSettingKey, birthdayAssigneeUserId.ToString(), now, ct);
        await UpsertSystemSettingAsync(AllowAdminCompleteAnyTaskSettingKey, request.AllowAdminCompleteAnyTask ? "true" : "false", now, ct);
        await UpsertSystemSettingAsync(FinancialDocumentsResponsibleUserSettingKey, financialDocumentsResponsibleUserId.ToString(), now, ct);
        await UpsertSystemSettingAsync(ApprovalDueDaysSettingKey, approvalDueDays.ToString(), now, ct);
        await UpsertSystemSettingAsync(TaskCardWindowModeSettingKey, taskCardWindowMode, now, ct);
        await UpsertSystemSettingAsync(ApprovalQuickActionModeSettingKey, approvalQuickActionMode, now, ct);
        await UpsertSystemSettingAsync(CaseInactivityAutomationEnabledSettingKey, request.CaseInactivityAutomationEnabled ? "true" : "false", now, ct);
        await UpsertSystemSettingAsync(CaseInactivityAutomationDaysSettingKey, inactivityDays.ToString(), now, ct);
        await UpsertSystemSettingAsync(CaseInactivityAutomationLimitSettingKey, inactivityLimit.ToString(), now, ct);
        await UpsertSystemSettingAsync(CaseInactivityAutomationDefaultAssigneeSettingKey, defaultAssigneeUserId, now, ct);
        await UpsertSystemSettingAsync(CaseInactivityAutomationEscalationDaysSettingKey, escalationDays.ToString(), now, ct);
        await UpsertSystemSettingAsync(CaseInactivityAutomationEscalationUserSettingKey, escalationUserId, now, ct);
        await UpsertSystemSettingAsync(CaseInactivityAutomationRunLocalTimeSettingKey, runLocalTime, now, ct);
        await UpsertSystemSettingAsync(CaseInactivityAutomationScheduleTimeZoneSettingKey, scheduleTimeZone, now, ct);
        if (normalizedStickerStyles is not null)
        {
            await UpsertSystemSettingAsync(TaskStickerStylesSettingKey, JsonSerializer.Serialize(normalizedStickerStyles, JsonOptions), now, ct);
        }
        await _db.SaveChangesAsync(ct);
        await _realtimePublisher.PublishTaskJournalChangedAsync(ct);

        return Ok(await GetTaskJournalSettingsAsync(ct));
    }

    [HttpGet("automation/case-control/diagnostics")]
    public async Task<ActionResult<TaskJournalAutomationDiagnosticsDto>> GetCaseControlAutomationDiagnostics(CancellationToken ct)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin && access.Permission?.CanView != true)
        {
            return Forbid();
        }

        var settings = await GetTaskJournalSettingsAsync(ct);
        var defaultAssignee = await ResolveConfiguredAutomationUserAsync(settings.CaseInactivityDefaultAssigneeUserId, ct);
        var escalationUser = await ResolveConfiguredAutomationUserAsync(settings.CaseInactivityEscalationUserId, ct);
        var now = DateTime.UtcNow;
        var scheduleTimeZone = CalendarTimeZoneHelper.ResolveTimeZone(settings.CaseInactivityScheduleTimeZone);
        var runLocalTime = TimeOnly.TryParseExact(
            settings.CaseInactivityRunLocalTime,
            "HH:mm",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsedRunLocalTime)
            ? parsedRunLocalTime
            : CaseInactivityAutomationSchedulePolicy.DefaultRunLocalTime;
        DateOnly? lastRunLocalDate = null;
        if (_systemJobRunState is not null)
        {
            var runStates = await _systemJobRunState.GetLastRunsAsync(
                [SystemJobKeys.CaseInactivityControl],
                ct);
            if (runStates.TryGetValue(SystemJobKeys.CaseInactivityControl, out var runState))
            {
                lastRunLocalDate = CaseInactivityAutomationSchedulePolicy.ResolveLastRunLocalDate(
                    runState.LastSuccessAt,
                    scheduleTimeZone);
            }
        }
        var schedule = CaseInactivityAutomationSchedulePolicy.Evaluate(
            now,
            runLocalTime,
            scheduleTimeZone,
            lastRunLocalDate);
        var inactivityCutoff = now.AddDays(-settings.CaseInactivityDays);
        var escalationCutoff = now.AddDays(-Math.Clamp(
            settings.CaseInactivityEscalationDays,
            settings.CaseInactivityDays,
            365));
        var vacationUserIds = await CaseInactivityResponsibleAvailabilityService
            .GetUsersOnVacationAsync(_db, now, ct);
        var defaultAssigneeAvailable = defaultAssignee is not null &&
            !vacationUserIds.Contains(defaultAssignee.Id);

        var visibleCases = CaseInactivityAutomationQuery.Build(
            _db,
            vacationUserIds,
            defaultAssigneeAvailable,
            inactivityCutoff,
            escalationCutoff,
            _db.ClientCases.ApplyClientCaseVisibility(access.Visibility));

        var candidateStatistics = settings.CaseInactivityAutomationEnabled
            ? await visibleCases
                .GroupBy(_ => 1)
                .Select(group => new
                {
                    CandidateCount = group.Count(item => item.IsCandidate),
                    UnassignedCount = group.Count(item => item.IsDueWithoutAssignee),
                    EscalationCount = group.Count(item => item.IsCriticalCandidate),
                })
                .SingleOrDefaultAsync(ct)
            : null;

        var auditQuery = _db.ObjectAuditEvents
            .AsNoTracking()
            .Where(item =>
                item.ObjectType == "client-case" &&
                item.Action == "Automation" &&
                item.MetadataJson != null &&
                item.MetadataJson.Contains("caseInactivityControlTaskCreated"));

        var createdSince = now.AddDays(-7);
        var auditStatistics = await auditQuery
            .GroupBy(_ => 1)
            .Select(group => new
            {
                CreatedCountLast7Days = group.Count(item => item.CreatedAt >= createdSince),
                LastCreatedAt = group.Max(item => (DateTime?)item.CreatedAt),
                LastCreatedDescription = group
                    .OrderByDescending(item => item.CreatedAt)
                    .Select(item => item.Description)
                    .FirstOrDefault(),
            })
            .SingleOrDefaultAsync(ct);

        var visibleCandidateCount = candidateStatistics?.CandidateCount ?? 0;
        var nextRunCandidateCount = Math.Min(visibleCandidateCount, settings.CaseInactivityLimit);

        return Ok(new TaskJournalAutomationDiagnosticsDto
        {
            Enabled = settings.CaseInactivityAutomationEnabled,
            InactivityDays = settings.CaseInactivityDays,
            EscalationDays = settings.CaseInactivityEscalationDays,
            Limit = settings.CaseInactivityLimit,
            DefaultAssigneeUserId = settings.CaseInactivityDefaultAssigneeUserId,
            DefaultAssigneeName = defaultAssignee is null ? string.Empty : BuildUserDisplayName(defaultAssignee.FullName, defaultAssignee.Email),
            DefaultAssigneeAvailable = defaultAssigneeAvailable,
            EscalationUserId = settings.CaseInactivityEscalationUserId,
            EscalationUserName = escalationUser is null ? string.Empty : BuildUserDisplayName(escalationUser.FullName, escalationUser.Email),
            VisibleCandidateCount = visibleCandidateCount,
            NextRunCandidateCount = nextRunCandidateCount,
            DeferredByLimitCount = visibleCandidateCount - nextRunCandidateCount,
            VisibleUnassignedCount = candidateStatistics?.UnassignedCount ?? 0,
            VisibleEscalationCandidateCount = candidateStatistics?.EscalationCount ?? 0,
            CreatedTaskCountLast7Days = auditStatistics?.CreatedCountLast7Days ?? 0,
            ScheduleDueNow = settings.CaseInactivityAutomationEnabled && schedule.ShouldRun,
            NextScheduledRunAt = settings.CaseInactivityAutomationEnabled
                ? ToIso(schedule.NextRunAtUtc)
                : null,
            LastCreatedAt = auditStatistics?.LastCreatedAt is { } lastCreatedAt ? ToIso(lastCreatedAt) : null,
            LastCreatedDescription = auditStatistics?.LastCreatedDescription,
        });
    }

    /// <summary>
    /// Возвращает счётчики журнала без загрузки полных карточек задач.
    /// Фильтры совпадают со списком, поэтому переключение метрики не требует
    /// последовательного чтения всех страниц и повторной фильтрации в браузере.
    /// </summary>
    [HttpGet("summary")]
    public async Task<ActionResult<TaskJournalListSummaryDto>> GetTaskListSummary(
        [FromQuery] string? search,
        [FromQuery] string? status,
        [FromQuery] string? type,
        [FromQuery] int? assigneeUserId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int? clientCaseId = null,
        [FromQuery] int? clientId = null,
        [FromQuery] int? legalEntityId = null,
        [FromQuery] string? priority = null,
        [FromQuery] bool? underControl = null,
        [FromQuery] int? authorUserId = null,
        [FromQuery] string? participantRole = null,
        [FromQuery] bool approvalScope = false,
        [FromQuery] DateTime? todayFrom = null,
        [FromQuery] DateTime? todayTo = null,
        CancellationToken ct = default)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin && access.Permission?.CanView != true)
        {
            return Forbid();
        }

        var now = DateTime.UtcNow;
        var normalizedTodayFrom = todayFrom ?? now.Date;
        var normalizedTodayTo = todayTo is { } requestedTodayTo && requestedTodayTo > normalizedTodayFrom
            ? requestedTodayTo
            : normalizedTodayFrom.AddDays(1);
        var query = BuildTaskJournalListQuery(
            access,
            search,
            status,
            type,
            assigneeUserId,
            from,
            to,
            clientCaseId,
            clientId,
            legalEntityId,
            priority,
            underControl,
            authorUserId,
            participantRole,
            approvalScope,
            metric: null,
            todayFrom: normalizedTodayFrom,
            todayTo: normalizedTodayTo);
        var unbilledQuery = BuildTaskJournalListQuery(
            access,
            search,
            status: null,
            type,
            assigneeUserId,
            from,
            to,
            clientCaseId,
            clientId,
            legalEntityId,
            priority,
            underControl,
            authorUserId,
            participantRole,
            approvalScope,
            metric: null,
            todayFrom: normalizedTodayFrom,
            todayTo: normalizedTodayTo,
            billingStatus: "unbilled");

        var aggregate = await query
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Active = group.Count(task =>
                    (task.Status == null || task.Status.ToLower() != "done") &&
                    (task.Status == null || task.Status.ToLower() != "cancelled")),
                Overdue = group.Count(task =>
                    (task.Status == null || task.Status.ToLower() != "done") &&
                    (task.Status == null || task.Status.ToLower() != "cancelled") &&
                    task.DueAt < now),
                Today = group.Count(task =>
                    (task.Status == null || task.Status.ToLower() != "done") &&
                    (task.Status == null || task.Status.ToLower() != "cancelled") &&
                    task.DueAt >= normalizedTodayFrom &&
                    task.DueAt < normalizedTodayTo),
                HighPriority = group.Count(task =>
                    (task.Status == null || task.Status.ToLower() != "done") &&
                    (task.Status == null || task.Status.ToLower() != "cancelled") &&
                    (task.Priority == "high" || task.Priority == "urgent")),
                Fixed = group.Count(task =>
                    task.ClientBillingClassification == TaskClientBillingClassification.Billable &&
                    !task.BillingInvoiceId.HasValue &&
                    task.BillingModeSnapshot != null &&
                    task.BillingModeSnapshot.ToLower() == ContractBillingMode.Fixed.ToLower() &&
                    (task.Status == null || task.Status.ToLower() != "done") &&
                    (task.Status == null || task.Status.ToLower() != "cancelled")),
                Invoiced = group.Count(task =>
                    task.ClientBillingClassification == TaskClientBillingClassification.Billable &&
                    task.BillingInvoiceId.HasValue &&
                    (task.Status == null || task.Status.ToLower() != "done") &&
                    (task.Status == null || task.Status.ToLower() != "cancelled")),
                NonBillable = group.Count(task =>
                    task.ClientBillingClassification == TaskClientBillingClassification.Internal &&
                    (task.Status == null || task.Status.ToLower() != "done") &&
                    (task.Status == null || task.Status.ToLower() != "cancelled")),
                Unclassified = group.Count(task =>
                    task.ClientBillingClassification == TaskClientBillingClassification.Legacy &&
                    (task.Status == null || task.Status.ToLower() != "done") &&
                    (task.Status == null || task.Status.ToLower() != "cancelled")),
            })
            .SingleOrDefaultAsync(ct);

        var unbilledOwnerCounts = await unbilledQuery
            .GroupBy(task => new { task.ClientId, task.LegalEntityId })
            .Select(group => group.Count())
            .ToListAsync(ct);
        var unbilledCount = unbilledOwnerCounts.Sum();
        var invoicesToPrepare = unbilledOwnerCounts.Count;

        return Ok(new TaskJournalListSummaryDto
        {
            Active = aggregate?.Active ?? 0,
            Overdue = aggregate?.Overdue ?? 0,
            Today = aggregate?.Today ?? 0,
            HighPriority = aggregate?.HighPriority ?? 0,
            Unbilled = unbilledCount,
            InvoicesToPrepare = invoicesToPrepare,
            Fixed = aggregate?.Fixed ?? 0,
            Invoiced = aggregate?.Invoiced ?? 0,
            NonBillable = aggregate?.NonBillable ?? 0,
            Unclassified = aggregate?.Unclassified ?? 0,
        });
    }

    [HttpGet("automation/case-control/preview")]
    public async Task<ActionResult<TaskJournalAutomationPreviewDto>> PreviewCaseControlAutomation(
        [FromQuery] int inactivityDays,
        [FromQuery] int escalationDays,
        CancellationToken ct,
        [FromQuery] int defaultAssigneeUserId = 0,
        [FromQuery] int dailyLimit = 25)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin)
        {
            return Forbid();
        }

        var normalizedInactivityDays = Math.Clamp(inactivityDays, 1, 365);
        var normalizedEscalationDays = Math.Clamp(escalationDays, normalizedInactivityDays, 365);
        var normalizedDailyLimit = Math.Clamp(dailyLimit, 1, 100);
        var now = DateTime.UtcNow;
        var inactivityCutoff = now.AddDays(-normalizedInactivityDays);
        var escalationCutoff = now.AddDays(-normalizedEscalationDays);
        var vacationUserIds = await CaseInactivityResponsibleAvailabilityService
            .GetUsersOnVacationAsync(_db, now, ct);
        var hasAvailableDefaultAssignee = await HasAvailableAutomationUserAsync(
            defaultAssigneeUserId,
            vacationUserIds,
            ct);

        var statistics = await CaseInactivityAutomationQuery.Build(
                _db,
                vacationUserIds,
                hasAvailableDefaultAssignee,
                inactivityCutoff,
                escalationCutoff)
            .GroupBy(_ => 1)
            .Select(group => new
            {
                TrackedCaseCount = group.Count(),
                ExcludedByStatusCount = group.Count(item => item.IsExcludedStatus),
                PausedByVacationCount = group.Count(item => item.IsPausedByVacation),
                DisabledCount = group.Count(item => item.IsDisabled),
                ActiveTaskCount = group.Count(item => item.IsBlockedByActiveTask),
                UnassignedCount = group.Count(item => item.IsUnassigned),
                RecentlyActiveCount = group.Count(item => item.IsRecentlyActive),
                CandidateCount = group.Count(item => item.IsCandidate),
                EscalationCandidateCount = group.Count(item => item.IsCriticalCandidate),
            })
            .SingleOrDefaultAsync(ct);

        var candidateCount = statistics?.CandidateCount ?? 0;
        var nextRunCandidateCount = Math.Min(candidateCount, normalizedDailyLimit);

        return Ok(new TaskJournalAutomationPreviewDto
        {
            InactivityDays = normalizedInactivityDays,
            EscalationDays = normalizedEscalationDays,
            DailyLimit = normalizedDailyLimit,
            TrackedCaseCount = statistics?.TrackedCaseCount ?? 0,
            CandidateCount = candidateCount,
            NextRunCandidateCount = nextRunCandidateCount,
            DeferredByLimitCount = candidateCount - nextRunCandidateCount,
            EscalationCandidateCount = statistics?.EscalationCandidateCount ?? 0,
            ExcludedByStatusCount = statistics?.ExcludedByStatusCount ?? 0,
            PausedByVacationCount = statistics?.PausedByVacationCount ?? 0,
            DisabledCount = statistics?.DisabledCount ?? 0,
            ActiveTaskCount = statistics?.ActiveTaskCount ?? 0,
            UnassignedCount = statistics?.UnassignedCount ?? 0,
            RecentlyActiveCount = statistics?.RecentlyActiveCount ?? 0,
        });
    }

    [HttpGet("automation/case-control/preview/details")]
    public async Task<ActionResult<TaskJournalAutomationPreviewDetailsDto>> GetCaseControlAutomationPreviewDetails(
        [FromQuery] int inactivityDays,
        [FromQuery] int escalationDays,
        [FromQuery] string category,
        [FromQuery] string? search = null,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 20,
        CancellationToken ct = default,
        [FromQuery] int defaultAssigneeUserId = 0)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin)
        {
            return Forbid();
        }

        var normalizedCategory = category?.Trim() ?? string.Empty;
        if (!AllowedAutomationPreviewCategories.Contains(normalizedCategory))
        {
            return BadRequest("Неизвестная категория предварительного расчёта.");
        }

        var normalizedInactivityDays = Math.Clamp(inactivityDays, 1, 365);
        var normalizedEscalationDays = Math.Clamp(escalationDays, normalizedInactivityDays, 365);
        var normalizedOffset = Math.Clamp(offset, 0, 1_000_000);
        var normalizedLimit = Math.Clamp(limit, 1, 50);
        var now = DateTime.UtcNow;
        var inactivityCutoff = now.AddDays(-normalizedInactivityDays);
        var escalationCutoff = now.AddDays(-normalizedEscalationDays);
        var responsibleAvailability = await CaseInactivityResponsibleAvailabilityService
            .GetAvailabilityAsync(_db, now, ct);
        var hasAvailableDefaultAssignee = await HasAvailableAutomationUserAsync(
            defaultAssigneeUserId,
            responsibleAvailability.UsersOnVacation,
            ct);

        var detailsQuery = CaseInactivityAutomationQuery.Build(
            _db,
            responsibleAvailability.UsersOnVacation,
            hasAvailableDefaultAssignee,
            inactivityCutoff,
            escalationCutoff);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var rawTerm = search.Trim();
            rawTerm = rawTerm.Length > 120 ? rawTerm[..120] : rawTerm;
            var term = rawTerm.ToLower();
            detailsQuery = detailsQuery.Where(item =>
                item.Title.ToLower().Contains(term) ||
                item.Title.Contains(rawTerm) ||
                item.ClientName.ToLower().Contains(term) ||
                item.ClientName.Contains(rawTerm) ||
                item.LegalEntityName.ToLower().Contains(term) ||
                item.LegalEntityName.Contains(rawTerm) ||
                item.ResponsibleName.ToLower().Contains(term) ||
                item.ResponsibleName.Contains(rawTerm));
        }

        detailsQuery = normalizedCategory switch
        {
            "criticalCandidate" => detailsQuery.Where(item => item.IsCriticalCandidate),
            "excludedStatus" => detailsQuery.Where(item => item.IsExcludedStatus),
            "vacation" => detailsQuery.Where(item => item.IsPausedByVacation),
            "disabled" => detailsQuery.Where(item => item.IsDisabled),
            "activeTask" => detailsQuery.Where(item => item.IsBlockedByActiveTask),
            "unassigned" => detailsQuery.Where(item => item.IsUnassigned),
            "recentActivity" => detailsQuery.Where(item => item.IsRecentlyActive),
            _ => detailsQuery.Where(item => item.IsCandidate),
        };

        var totalCount = await detailsQuery.CountAsync(ct);
        var sortableDetailsQuery = detailsQuery.Select(item => new
        {
            Item = item,
            item.LastMeaningfulActivityAt,
            item.LastControlAnchorAt,
        });

        var orderedDetailsQuery = normalizedCategory switch
        {
            "criticalCandidate" => sortableDetailsQuery.OrderBy(row => row.LastMeaningfulActivityAt),
            "candidate" or "recentActivity" => sortableDetailsQuery.OrderBy(row => row.LastControlAnchorAt),
            _ => sortableDetailsQuery.OrderBy(row => row.Item.ClientName != string.Empty
                ? row.Item.ClientName
                : row.Item.LegalEntityName),
        };

        var rows = await orderedDetailsQuery
            .ThenBy(row => row.Item.ClientName != string.Empty ? row.Item.ClientName : row.Item.LegalEntityName)
            .ThenBy(row => row.Item.Title)
            .ThenBy(row => row.Item.Id)
            .Select(row => row.Item)
            .Skip(normalizedOffset)
            .Take(normalizedLimit)
            .ToListAsync(ct);

        var items = rows.Select(item =>
        {
            var lastActivity = CaseMeaningfulActivityResolver.Resolve(
                new CaseMeaningfulActivitySignals(
                    item.CreatedAt,
                    LastTaskAt: item.LastTaskAt,
                    LastTaskCommentAt: item.LastTaskCommentAt,
                    LastDocumentAt: item.LastDocumentAt,
                    LastCourtEventAt: item.LastCourtEventAt,
                    LastResponsibleViewAt: item.LastViewedAt,
                    LastActivityConfirmationAt: item.ActivityConfirmedAt));
            var controlAnchorIsLater = item.LastControlCycleEndedAt.HasValue &&
                                       item.LastControlCycleEndedAt.Value > lastActivity.At;
            var lastControlAnchorAt = controlAnchorIsLater
                ? item.LastControlCycleEndedAt!.Value
                : lastActivity.At;
            var lastControlAnchorSource = controlAnchorIsLater
                ? "controlCycleEnded"
                : lastActivity.Source;
            var daysSinceAnchor = Math.Max(0, (int)Math.Floor((now - lastControlAnchorAt).TotalDays));
            var daysSinceMeaningfulActivity = Math.Max(0, (int)Math.Floor((now - lastActivity.At).TotalDays));
            var explanation = normalizedCategory switch
            {
                "excludedStatus" => $"Статус «{ClientCaseStatusLabel(item.Status)}» исключает дело из автоконтроля.",
                "vacation" => item.ResponsibleUserId is int responsibleUserId &&
                              responsibleAvailability.VacationUntilDates.TryGetValue(responsibleUserId, out var vacationUntil)
                    ? $"Ответственный отсутствует по {vacationUntil}; проверка продолжится после возвращения."
                    : "Ответственный временно отсутствует; проверка продолжится после возвращения.",
                "disabled" => string.IsNullOrWhiteSpace(item.InactivityRemindersDisabledReason)
                    ? "Автоконтроль индивидуально выключен в карточке дела."
                    : $"Автоконтроль выключен: {item.InactivityRemindersDisabledReason}",
                "activeTask" => $"По делу есть активные задачи: {item.ActiveTaskCount}.",
                "unassigned" => "У дела нет активного ответственного, и резервный исполнитель автоконтроля не выбран.",
                "recentActivity" => $"Последнее значимое движение было {daysSinceAnchor} дн. назад; порог ещё не наступил.",
                "criticalCandidate" => $"Значимого движения нет {daysSinceMeaningfulActivity} дн.; достигнут критический порог {normalizedEscalationDays} дн.",
                _ => $"Значимого движения нет {daysSinceAnchor} дн.; срок постановки контроля наступил.",
            };

            return new TaskJournalAutomationPreviewCaseDto
            {
                ClientCaseId = item.Id,
                ClientCaseTitle = item.Title,
                ClientId = item.ClientId,
                ClientName = item.ClientName,
                LegalEntityId = item.LegalEntityId,
                LegalEntityName = item.LegalEntityName,
                ResponsibleName = item.ResponsibleName,
                Explanation = explanation,
                LastActivityAt = ToIso(lastControlAnchorAt),
                LastActivitySource = lastControlAnchorSource,
                FirstControlDueAt = ToIso(lastControlAnchorAt.AddDays(normalizedInactivityDays)),
                CriticalControlDueAt = ToIso(lastActivity.At.AddDays(normalizedEscalationDays)),
            };
        }).ToList();

        return Ok(new TaskJournalAutomationPreviewDetailsDto
        {
            Category = normalizedCategory,
            InactivityDays = normalizedInactivityDays,
            EscalationDays = normalizedEscalationDays,
            TotalCount = totalCount,
            Offset = normalizedOffset,
            Limit = normalizedLimit,
            Items = items,
        });
    }

    [HttpGet("calendar.ics")]
    public async Task<IActionResult> ExportCalendar(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int? clientCaseId,
        [FromQuery] int? clientId,
        [FromQuery] int? legalEntityId,
        [FromQuery] string? reminderMinutes,
        CancellationToken ct)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin && access.Permission?.CanView != true)
        {
            return Forbid();
        }

        if (!access.IsAdmin && access.Permission?.CanExport != true)
        {
            return Forbid();
        }

        var query = BuildVisibleTasksQuery(access)
            .AsNoTracking()
            .Where(task => task.Status != "cancelled");

        if (from.HasValue)
        {
            query = query.Where(task => task.DueAt >= from.Value);
        }

        if (to.HasValue)
        {
            query = query.Where(task => task.DueAt <= to.Value);
        }

        if (clientCaseId.HasValue)
        {
            query = query.Where(task => task.ClientCaseId == clientCaseId.Value);
        }

        if (clientId.HasValue)
        {
            query = query.Where(task => task.ClientId == clientId.Value);
        }

        if (legalEntityId.HasValue)
        {
            query = query.Where(task => task.LegalEntityId == legalEntityId.Value);
        }

        var calendarSyncSettings = await LoadCalendarSyncSettingsAsync(ct);
        if (string.Equals(calendarSyncSettings.CompletedTaskCalendarAction, "hide", StringComparison.Ordinal))
        {
            query = query.Where(task => task.Status != "done");
        }

        var tasks = await query
            .OrderBy(task => task.DueAt)
            .ThenBy(task => task.Id)
            .Take(1000)
            .ToListAsync(ct);

        var reminders = IcsReminderOptions.Normalize(reminderMinutes);
        var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct);
        var calendar = BuildIcsCalendar(tasks, reminders, timeZone);
        _db.ObjectAuditEvents.Add(CalendarExportAudit.CreateExportEvent(
            User,
            "tasks",
            $"Выгружен ICS-календарь задач: {tasks.Count} событий.",
            tasks.Count,
            reminders,
            new
            {
                from,
                to,
                clientCaseId,
            }));
        await _db.SaveChangesAsync(ct);

        return File(
            Encoding.UTF8.GetBytes(calendar),
            "text/calendar; charset=utf-8",
            "task-calendar.ics");
    }

    [HttpGet("planning-report.{format}")]
    public async Task<IActionResult> ExportPlanningReport(
        string format,
        [FromQuery] string? search,
        [FromQuery] string? status,
        [FromQuery] string? type,
        [FromQuery] int? assigneeUserId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int? clientCaseId = null,
        [FromQuery] int? clientId = null,
        [FromQuery] int? legalEntityId = null,
        [FromQuery] string? priority = null,
        CancellationToken ct = default)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin && access.Permission?.CanView != true)
        {
            return Forbid();
        }

        if (!access.IsAdmin && access.Permission?.CanExport != true)
        {
            return Forbid();
        }

        var normalizedFormat = NormalizePlanningReportFormat(format);
        if (normalizedFormat is null)
        {
            return BadRequest("Поддерживаются форматы html, word и pdf.");
        }

        var tasks = await ApplyTaskJournalFilters(
                BuildVisibleTasksQuery(access).AsNoTracking(),
                search,
                status,
                type,
                assigneeUserId,
                from,
                to,
                clientCaseId,
                clientId,
                legalEntityId,
                priority)
            .OrderBy(task => task.Client != null ? task.Client.Name : task.LegalEntity != null ? task.LegalEntity.ShortName : task.ObjectName)
            .ThenBy(task => task.ClientCase != null ? task.ClientCase.Title : task.ObjectName)
            .ThenBy(task => task.DueAt)
            .ThenBy(task => task.Id)
            .Take(5000)
            .ToListAsync(ct);

        var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct);
        var generatedAtUtc = NormalizeUtcInstant(DateTime.UtcNow);
        var generatedAtLocal = CalendarTimeZoneHelper.ToUserLocal(generatedAtUtc, timeZone);
        var filterText = BuildTaskPlanningReportFilterText(status, type, priority, from, to);
        var fileStamp = generatedAtLocal.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);

        if (normalizedFormat == "pdf")
        {
            if (_pdfService is null)
            {
                return Problem("Сервис формирования PDF недоступен.");
            }

            var html = BuildTaskPlanningReportHtml(tasks, filterText, generatedAtUtc, timeZone);
            var pdfBytes = await _pdfService.RenderAsync(html, ct);
            return File(pdfBytes, "application/pdf", $"task-planning-report-{fileStamp}.pdf");
        }

        if (normalizedFormat == "word")
        {
            var document = BuildTaskPlanningWordDocument(tasks, filterText, generatedAtUtc, timeZone);
            return File(
                document,
                HtmlWordDocumentBuilder.ContentType,
                $"task-planning-report-{fileStamp}.docx");
        }

        var htmlBytes = Encoding.UTF8.GetBytes(BuildTaskPlanningReportHtml(tasks, filterText, generatedAtUtc, timeZone));
        return File(htmlBytes, "text/html; charset=utf-8", $"task-planning-report-{fileStamp}.html");
    }

    [HttpGet("automation/case-candidates")]
    public async Task<ActionResult<IReadOnlyList<TaskJournalCaseAutomationCandidateDto>>> GetAutomationCaseCandidates(
        [FromQuery] int? inactiveDays = null,
        [FromQuery] int? limit = null,
        [FromQuery] int offset = 0,
        [FromQuery] int? escalationDays = null,
        [FromQuery] string? search = null,
        [FromQuery] bool includeWaiting = false,
        [FromQuery] string? automationState = null,
        CancellationToken ct = default)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin && access.Permission?.CanView != true)
        {
            return Forbid();
        }

        var settings = await GetTaskJournalSettingsAsync(ct);
        if (!settings.CaseInactivityAutomationEnabled)
        {
            return Ok(Array.Empty<TaskJournalCaseAutomationCandidateDto>());
        }

        var effectiveInactiveDays = Math.Clamp(inactiveDays ?? settings.CaseInactivityDays, 1, 365);
        var effectiveLimit = Math.Clamp(limit ?? settings.CaseInactivityLimit, 1, 100);
        var effectiveOffset = Math.Clamp(offset, 0, 1_000_000);
        var effectiveEscalationDays = Math.Clamp(escalationDays ?? settings.CaseInactivityEscalationDays, effectiveInactiveDays, 365);
        var normalizedAutomationState = automationState?.Trim().ToLowerInvariant();
        if (normalizedAutomationState is not null and not "all" and not "critical" and not "ready" and not "waiting" and not "paused")
        {
            return BadRequest("Неизвестное состояние автоконтроля.");
        }
        var now = DateTime.UtcNow;
        var cutoff = now.AddDays(-effectiveInactiveDays);
        var escalationCutoff = now.AddDays(-effectiveEscalationDays);
        var responsibleAvailability = await CaseInactivityResponsibleAvailabilityService
            .GetAvailabilityAsync(_db, now, ct);
        var vacationUserIds = responsibleAvailability.UsersOnVacation;

        var candidates = CaseInactivityAutomationQuery.Build(
                _db,
                vacationUserIds,
                hasAvailableDefaultAssignee: true,
                cutoff,
                escalationCutoff,
                _db.ClientCases.ApplyClientCaseVisibility(access.Visibility),
                includeCompletedTaskCount: true)
            .Where(item => !item.IsExcludedStatus);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var rawTerm = search.Trim();
            rawTerm = rawTerm.Length > 120 ? rawTerm[..120] : rawTerm;
            var term = rawTerm.ToLower();
            candidates = candidates.Where(item =>
                item.Title.ToLower().Contains(term) ||
                item.Title.Contains(rawTerm) ||
                item.ClientName.ToLower().Contains(term) ||
                item.ClientName.Contains(rawTerm) ||
                item.LegalEntityName.ToLower().Contains(term) ||
                item.LegalEntityName.Contains(rawTerm) ||
                item.ResponsibleName.ToLower().Contains(term) ||
                item.ResponsibleName.Contains(rawTerm));
        }

        var stateQuery = candidates.Where(item => item.ActiveTaskCount == 0);

        if (normalizedAutomationState == "critical")
        {
            stateQuery = stateQuery.Where(item => item.IsCriticalCandidate);
        }
        else if (normalizedAutomationState == "paused")
        {
            stateQuery = stateQuery.Where(item => item.IsDisabled || item.IsPausedByVacation);
        }
        else if (normalizedAutomationState == "ready" || (normalizedAutomationState is null && !includeWaiting))
        {
            stateQuery = stateQuery.Where(item => item.IsCandidate);
        }
        else if (normalizedAutomationState == "waiting")
        {
            stateQuery = stateQuery.Where(item => item.IsRecentlyActive);
        }

        var includeAllStates = normalizedAutomationState == "all" ||
            (normalizedAutomationState is null && includeWaiting);
        var orderedStateQuery = normalizedAutomationState == "critical"
            ? stateQuery.OrderBy(item => item.LastMeaningfulActivityAt)
            : includeAllStates
            ? stateQuery
                .OrderBy(item =>
                    item.IsDisabled || item.IsPausedByVacation
                        ? 3
                        : item.IsCandidate
                            ? item.IsCriticalCandidate
                                ? 0
                                : 1
                            : 2)
                .ThenBy(item => item.LastControlAnchorAt)
            : stateQuery.OrderBy(item => item.LastControlAnchorAt);

        var filteredCandidates = await orderedStateQuery
            .ThenBy(item => item.ClientName != string.Empty ? item.ClientName : item.LegalEntityName)
            .ThenBy(item => item.Title)
            .Skip(effectiveOffset)
            .Take(effectiveLimit)
            .ToListAsync(ct);

        var availableUsers = await LoadAvailableAutomationUsersAsync(
            filteredCandidates
                .Select(item => item.ResponsibleUserId)
                .Append(settings.CaseInactivityDefaultAssigneeUserId),
            ct);
        var configuredDefaultAssignee = GetAvailableAutomationUser(
            availableUsers,
            settings.CaseInactivityDefaultAssigneeUserId);
        if (configuredDefaultAssignee is not null && vacationUserIds.Contains(configuredDefaultAssignee.Id))
        {
            configuredDefaultAssignee = null;
        }
        var availableResponsibleUsersByCaseId = filteredCandidates.ToDictionary(
            item => item.Id,
            item => vacationUserIds.Contains(item.ResponsibleUserId ?? 0)
                ? null
                : GetAvailableAutomationUser(availableUsers, item.ResponsibleUserId));
        var suggestedAssigneesByCaseId = availableResponsibleUsersByCaseId.ToDictionary(
            item => item.Key,
            item => BuildAutomationAssignee(
                item.Value?.Id,
                availableUsers,
                configuredDefaultAssignee,
                access));
        var suggestedTimeZonesByUserId = suggestedAssigneesByCaseId.Values
            .Select(assignee => assignee.UserId)
            .Where(userId => userId.HasValue)
            .Select(userId => userId!.Value)
            .Distinct()
            .ToDictionary(
                userId => userId,
                userId => CalendarTimeZoneHelper.ResolveTimeZone(
                    responsibleAvailability.TimeZoneIds.GetValueOrDefault(userId)));
        var defaultTimeZone = CalendarTimeZoneHelper.ResolveTimeZone(null);

        return Ok(filteredCandidates.Select(item =>
        {
            var lastActivity = CaseMeaningfulActivityResolver.Resolve(
                new CaseMeaningfulActivitySignals(
                    item.CreatedAt,
                    LastTaskAt: item.LastTaskAt,
                    LastTaskCommentAt: item.LastTaskCommentAt,
                    LastDocumentAt: item.LastDocumentAt,
                    LastCourtEventAt: item.LastCourtEventAt,
                    LastResponsibleViewAt: item.LastViewedAt,
                    LastActivityConfirmationAt: item.ActivityConfirmedAt));

            var daysWithoutActiveTask = Math.Max(0, (int)Math.Floor((now - lastActivity.At).TotalDays));
            var nextCycle = CaseInactivityControlCyclePolicy.EvaluateNextCycle(
                now,
                lastActivity.At,
                item.LastControlCycleEndedAt,
                effectiveInactiveDays);
            var isOnVacation = vacationUserIds.Contains(item.ResponsibleUserId ?? 0);
            var responsibleVacationUntilDate = item.ResponsibleUserId is int responsibleUserId &&
                                               responsibleAvailability.VacationUntilDates.TryGetValue(
                                                   responsibleUserId,
                                                   out var vacationUntilDate)
                ? vacationUntilDate
                : null;
            var isPaused = !item.InactivityRemindersEnabled || isOnVacation;
            var isReadyForControl = !isPaused && nextCycle.IsEligible;
            var isEscalated = isReadyForControl && daysWithoutActiveTask >= effectiveEscalationDays;
            var daysUntilControl = isReadyForControl || isPaused
                ? 0
                : Math.Max(1, (int)Math.Ceiling((nextCycle.EligibleAt - now).TotalDays));
            var reason = !item.InactivityRemindersEnabled
                ? string.IsNullOrWhiteSpace(item.InactivityRemindersDisabledReason)
                    ? "Автоконтроль по делу отключён"
                    : $"Автоконтроль отключён: {item.InactivityRemindersDisabledReason}"
                : isOnVacation
                    ? "Автоконтроль приостановлен на время отсутствия ответственного"
                : !nextCycle.IsEligible
                    ? nextCycle.WaitsAfterPreviousCycle
                        ? $"Новый контроль через {daysUntilControl} дн. после завершённого цикла"
                        : $"Первый контроль через {daysUntilControl} дн."
                : nextCycle.WaitsAfterPreviousCycle
                ? $"Предыдущий цикл завершён {Math.Max(0, (int)Math.Floor((now - nextCycle.CooldownAnchorAt).TotalDays))} дн. назад, нового движения нет"
                : item.LastTaskAt is null
                ? "По делу еще не было задач"
                : daysWithoutActiveTask >= effectiveInactiveDays
                    ? $"Нет активной задачи уже {daysWithoutActiveTask} дн."
                    : "Нет активной задачи";
            var suggestedAssignee = suggestedAssigneesByCaseId[item.Id];
            var suggestedAssigneeSource = availableResponsibleUsersByCaseId[item.Id] is not null
                ? "responsible"
                : configuredDefaultAssignee is not null
                    ? "configuredDefault"
                    : "currentUser";
            var suggestedTimeZone = suggestedAssignee.UserId is int suggestedAssigneeUserId &&
                                    suggestedTimeZonesByUserId.TryGetValue(suggestedAssigneeUserId, out var resolvedTimeZone)
                ? resolvedTimeZone
                : defaultTimeZone;

            return new TaskJournalCaseAutomationCandidateDto
            {
                ClientId = item.ClientId,
                ClientName = item.ClientName,
                LegalEntityId = item.LegalEntityId,
                LegalEntityName = item.LegalEntityName,
                ResponsibleUserId = item.ResponsibleUserId,
                ResponsibleName = item.ResponsibleName,
                ClientCaseId = item.Id,
                ClientCaseTitle = item.Title,
                Reason = reason,
                ActiveTaskCount = item.ActiveTaskCount,
                CompletedTaskCount = item.CompletedTaskCount,
                DaysWithoutActiveTask = daysWithoutActiveTask,
                IsEscalated = isEscalated,
                IsReadyForControl = isReadyForControl,
                AutomationState = isPaused ? "paused" : isReadyForControl ? "ready" : "waiting",
                LastActivityAt = ToIso(lastActivity.At),
                LastActivitySource = lastActivity.Source,
                LastTaskAt = item.LastTaskAt.HasValue ? ToIso(item.LastTaskAt.Value) : null,
                NextControlAt = isPaused ? null : ToIso(nextCycle.EligibleAt),
                DaysUntilControl = daysUntilControl,
                SuggestedTitle = "Проверить движение по делу",
                SuggestedDescription = BuildAutomationTaskDescription(isEscalated),
                SuggestedDueAt = ToIso(BuildDefaultTaskDueAtUtc(now, suggestedTimeZone)),
                SuggestedPriority = ResolveAutomationPriority(daysWithoutActiveTask, effectiveInactiveDays, effectiveEscalationDays),
                SuggestedAssigneeUserId = suggestedAssignee.UserId,
                SuggestedAssigneeName = suggestedAssignee.DisplayName,
                SuggestedAssigneeSource = suggestedAssigneeSource,
                SuggestedAssigneeTimeZoneId = CalendarTimeZoneHelper.ResolveIanaTimeZoneId(suggestedTimeZone),
                ResponsibleVacationUntilDate = responsibleVacationUntilDate,
            };
        }).ToList());
    }

    [HttpGet("automation/case-candidates/summary")]
    public async Task<ActionResult<TaskJournalCaseAutomationSummaryDto>> GetAutomationCaseCandidatesSummary(
        [FromQuery] int? inactiveDays = null,
        [FromQuery] int? escalationDays = null,
        [FromQuery] string? search = null,
        CancellationToken ct = default)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin && access.Permission?.CanView != true)
        {
            return Forbid();
        }

        var settings = await GetTaskJournalSettingsAsync(ct);
        if (!settings.CaseInactivityAutomationEnabled)
        {
            return Ok(new TaskJournalCaseAutomationSummaryDto());
        }

        var effectiveInactiveDays = Math.Clamp(inactiveDays ?? settings.CaseInactivityDays, 1, 365);
        var effectiveEscalationDays = Math.Clamp(
            escalationDays ?? settings.CaseInactivityEscalationDays,
            effectiveInactiveDays,
            365);
        var now = DateTime.UtcNow;
        var cutoff = now.AddDays(-effectiveInactiveDays);
        var escalationCutoff = now.AddDays(-effectiveEscalationDays);
        var responsibleAvailability = await CaseInactivityResponsibleAvailabilityService
            .GetAvailabilityAsync(_db, now, ct);

        var candidates = CaseInactivityAutomationQuery.Build(
                _db,
                responsibleAvailability.UsersOnVacation,
                hasAvailableDefaultAssignee: true,
                cutoff,
                escalationCutoff,
                _db.ClientCases.ApplyClientCaseVisibility(access.Visibility))
            .Where(item => !item.IsExcludedStatus);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var rawTerm = search.Trim();
            rawTerm = rawTerm.Length > 120 ? rawTerm[..120] : rawTerm;
            var term = rawTerm.ToLower();
            candidates = candidates.Where(item =>
                item.Title.ToLower().Contains(term) ||
                item.Title.Contains(rawTerm) ||
                item.ClientName.ToLower().Contains(term) ||
                item.ClientName.Contains(rawTerm) ||
                item.LegalEntityName.ToLower().Contains(term) ||
                item.LegalEntityName.Contains(rawTerm) ||
                item.ResponsibleName.ToLower().Contains(term) ||
                item.ResponsibleName.Contains(rawTerm));
        }

        var summaryData = await candidates
            .Where(item => item.ActiveTaskCount == 0)
            .GroupBy(_ => 1)
            .Select(group => new
            {
                TotalCount = group.Count(),
                PausedCount = group.Count(item => item.IsDisabled || item.IsPausedByVacation),
                ReadyCount = group.Count(item => item.IsCandidate),
                CriticalCount = group.Count(item => item.IsCriticalCandidate),
                WaitingCount = group.Count(item => item.IsRecentlyActive),
                NextWaitingAnchorAt = group
                    .Where(item => item.IsRecentlyActive)
                    .Min(item => (DateTime?)item.LastControlAnchorAt),
                NextCriticalActivityAt = group
                    .Where(item => item.IsCandidate && !item.IsCriticalCandidate)
                    .Min(item => (DateTime?)item.LastMeaningfulActivityAt),
            })
            .SingleOrDefaultAsync(ct);

        if (summaryData is null)
        {
            return Ok(new TaskJournalCaseAutomationSummaryDto());
        }

        var nextWaitingAt = summaryData.NextWaitingAnchorAt?.AddDays(effectiveInactiveDays);
        var nextCriticalAt = summaryData.NextCriticalActivityAt?.AddDays(effectiveEscalationDays);
        DateTime? nextVacationResumeAt = null;
        if (summaryData.PausedCount > 0 && responsibleAvailability.VacationResumeAtUtc.Count > 0)
        {
            var pausedResponsibleUserIds = await candidates
                .Where(item =>
                    item.ActiveTaskCount == 0 &&
                    item.IsPausedByVacation &&
                    item.InactivityRemindersEnabled &&
                    item.ResponsibleUserId.HasValue)
                .Select(item => item.ResponsibleUserId!.Value)
                .Distinct()
                .ToListAsync(ct);
            nextVacationResumeAt = pausedResponsibleUserIds
                .Select(userId => responsibleAvailability.VacationResumeAtUtc.TryGetValue(userId, out var resumeAt)
                    ? (DateTime?)resumeAt
                    : null)
                .Where(resumeAt => resumeAt.HasValue)
                .OrderBy(resumeAt => resumeAt)
                .FirstOrDefault();
        }

        DateTime? nextStateChangeAt = null;
        string? nextStateChangeKind = null;
        ConsiderNextStateChange(nextWaitingAt, "ready");
        ConsiderNextStateChange(nextCriticalAt, "critical");
        ConsiderNextStateChange(nextVacationResumeAt, "vacationResume");

        return Ok(new TaskJournalCaseAutomationSummaryDto
        {
            TotalCount = summaryData.TotalCount,
            PausedCount = summaryData.PausedCount,
            ReadyCount = summaryData.ReadyCount,
            CriticalCount = summaryData.CriticalCount,
            WaitingCount = summaryData.WaitingCount,
            NextStateChangeAt = nextStateChangeAt.HasValue ? ToIso(nextStateChangeAt.Value) : null,
            NextStateChangeKind = nextStateChangeKind,
        });

        void ConsiderNextStateChange(DateTime? candidateAt, string candidateKind)
        {
            if (!candidateAt.HasValue ||
                (nextStateChangeAt.HasValue && candidateAt.Value >= nextStateChangeAt.Value))
            {
                return;
            }

            nextStateChangeAt = candidateAt.Value;
            nextStateChangeKind = candidateKind;
        }
    }

    [HttpPost("automation/case-candidates/{clientCaseId:int}/task")]
    public async Task<ActionResult<TaskJournalItemDto>> CreateAutomationTaskForCase(int clientCaseId, CancellationToken ct)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin && access.Permission?.CanCreate != true)
        {
            return Forbid();
        }

        using var mutationLease = await _caseInactivityAutomationMutationGate.AcquireAsync(ct);

        var clientCase = await _db.ClientCases
            .Include(item => item.Client)
            .Include(item => item.LegalEntity)
            .ApplyClientCaseVisibility(access.Visibility)
            .FirstOrDefaultAsync(item =>
                item.Id == clientCaseId &&
                ((item.ClientId.HasValue && item.Client != null) ||
                 (item.LegalEntityId.HasValue && item.LegalEntity != null)) &&
                !CaseInactivityControlCyclePolicy.ExcludedCaseStatuses.Contains(item.Status),
                ct);
        if (clientCase is null)
        {
            return NotFound(new { message = "Дело для автоконтроля не найдено или уже закрыто." });
        }

        var now = DateTime.UtcNow;
        var responsibleUserId = clientCase.ResponsibleUserId
            ?? clientCase.Client?.ResponsibleUserId
            ?? clientCase.LegalEntity?.ResponsibleUserId;
        var vacationUserIds = await CaseInactivityResponsibleAvailabilityService
            .GetUsersOnVacationAsync(_db, now, ct);
        if (responsibleUserId.HasValue && vacationUserIds.Contains(responsibleUserId.Value))
        {
            return Conflict(new
            {
                message = "Ответственный по делу находится в отпуске. Автоконтроль будет продолжен после его возвращения.",
            });
        }

        var existingActiveTask = await _db.TaskJournalItems
            .AsNoTracking()
            .Where(task =>
                task.ClientCaseId == clientCase.Id &&
                ActiveTaskStatuses.Contains(task.Status))
            .OrderBy(task => task.DueAt)
            .FirstOrDefaultAsync(ct);
        if (existingActiveTask is not null)
        {
            return Conflict(new
            {
                message = "По делу уже есть активная задача.",
                taskId = existingActiveTask.Id,
            });
        }

        var settings = await GetTaskJournalSettingsAsync(ct);
        if (!settings.CaseInactivityAutomationEnabled || !clientCase.InactivityRemindersEnabled)
        {
            return BadRequest(new { message = "Автоконтроль для этого дела отключен." });
        }

        var signals = await CaseInactivityAutomationQuery.Build(
                _db,
                vacationUserIds,
                hasAvailableDefaultAssignee: true,
                now.AddDays(-settings.CaseInactivityDays),
                now.AddDays(-settings.CaseInactivityEscalationDays),
                _db.ClientCases.Where(item => item.Id == clientCase.Id))
            .SingleAsync(ct);

        var lastActivity = ResolveLastCaseActivity(
            clientCase,
            signals.LastTaskAt,
            signals.LastTaskCommentAt,
            signals.LastDocumentAt,
            signals.LastCourtEventAt);
        var nextCycle = CaseInactivityControlCyclePolicy.EvaluateNextCycle(
            now,
            lastActivity,
            signals.LastControlCycleEndedAt,
            settings.CaseInactivityDays);
        var daysWithoutActiveTask = nextCycle.DaysWithoutMeaningfulActivity;
        if (!nextCycle.IsEligible)
        {
            return Conflict(new
            {
                message = nextCycle.WaitsAfterPreviousCycle
                    ? "Предыдущий цикл автоконтроля недавно завершён. Новый цикл пока не требуется."
                    : "По делу недавно было движение. Контрольная задача пока не требуется.",
                daysWithoutActiveTask,
                nextControlAt = nextCycle.EligibleAt,
            });
        }

        var isEscalated = daysWithoutActiveTask >= settings.CaseInactivityEscalationDays;
        var availableUsers = await LoadAvailableAutomationUsersAsync(
            new int?[]
            {
                responsibleUserId,
                settings.CaseInactivityDefaultAssigneeUserId,
                isEscalated ? settings.CaseInactivityEscalationUserId : null,
            },
            ct);
        var configuredDefaultAssignee = GetAvailableAutomationUser(
            availableUsers,
            settings.CaseInactivityDefaultAssigneeUserId);
        if (configuredDefaultAssignee is not null && vacationUserIds.Contains(configuredDefaultAssignee.Id))
        {
            configuredDefaultAssignee = null;
        }
        var assignee = BuildAutomationAssignee(
            responsibleUserId,
            availableUsers,
            configuredDefaultAssignee,
            access);
        var escalationUser = isEscalated
            ? GetAvailableAutomationUser(availableUsers, settings.CaseInactivityEscalationUserId)
            : null;
        var binding = BuildCaseBinding(clientCase);
        if (binding.Error is not null)
        {
            return NotFound(new { message = "Дело для автоконтроля не найдено или уже закрыто." });
        }

        var dueAt = await BuildDefaultTaskDueAtUtcAsync(assignee.UserId, ct);
        var entity = new TaskJournalItem
        {
            ClientId = binding.ClientId,
            LegalEntityId = binding.LegalEntityId,
            ClientCaseId = clientCase.Id,
            ObjectName = BuildObjectName(binding.OwnerName, clientCase.Title),
            Title = "Проверить движение по делу",
            Description = BuildAutomationTaskDescription(isEscalated),
            AssigneeUserId = assignee.UserId,
            AssigneeName = assignee.DisplayName,
            AuthorUserId = access.UserId,
            AuthorName = access.DisplayName,
            DueAt = dueAt,
            PlannedDurationMinutes = 60,
            Type = "control",
            Priority = ResolveAutomationPriority(daysWithoutActiveTask, settings.CaseInactivityDays, settings.CaseInactivityEscalationDays),
            Status = "new",
            Result = null,
            RemindersJson = SerializeList(["before1d", "atDue"], AllowedReminders, ["atDue"]),
            ChannelsJson = SerializeList(["inApp"], AllowedChannels, ["inApp"]),
            TagsJson = SerializeFreeList([CaseInactivityAutomationTag]),
            InactivityControlCycleNumber = 1,
            InactivityControlNextCheckAt = now.AddDays(CaseInactivityControlStateMachine.FirstRepeatOffsetDays),
            CreatedAt = now,
            UpdatedAt = now,
            Participants = [ToParticipantEntity(assignee)],
        };
        AddEscalationObserver(entity, escalationUser, assignee.UserId, now);

        AddTaskHistory(entity, access, "Задача создана автоматически из автоконтроля дела.");
        _db.TaskJournalItems.Add(entity);
        await _db.SaveChangesAsync(ct);
        await _realtimePublisher.PublishTaskJournalChangedAsync(ct);
        return Created($"/api/tasks/{entity.Id}", Map(entity, binding));
    }

    [HttpPost("automation/case-candidates/tasks")]
    public async Task<ActionResult<TaskJournalAutomationCreateResultDto>> CreateAutomationTasksForCandidates(
        [FromQuery] int limit = 20,
        [FromQuery] string? search = null,
        [FromQuery] string? automationState = null,
        CancellationToken ct = default)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin && access.Permission?.CanCreate != true)
        {
            return Forbid();
        }

        using var mutationLease = await _caseInactivityAutomationMutationGate.AcquireAsync(ct);

        limit = Math.Clamp(limit, 1, 100);
        var normalizedAutomationState = automationState?.Trim().ToLowerInvariant();
        if (normalizedAutomationState is not null and not "all" and not "ready" and not "critical")
        {
            return BadRequest("Массовое создание доступно только для наступивших или критических дел.");
        }
        var settings = await GetTaskJournalSettingsAsync(ct);
        if (!settings.CaseInactivityAutomationEnabled)
        {
            return Ok(new TaskJournalAutomationCreateResultDto
            {
                CreatedTasks = Array.Empty<TaskJournalItemDto>(),
                SkippedCount = 0,
            });
        }

        var effectiveLimit = Math.Min(limit, settings.CaseInactivityLimit);
        var now = DateTime.UtcNow;
        var cutoff = now.AddDays(-settings.CaseInactivityDays);
        var escalationCutoff = now.AddDays(-Math.Clamp(
            settings.CaseInactivityEscalationDays,
            settings.CaseInactivityDays,
            365));
        var vacationUserIds = await CaseInactivityResponsibleAvailabilityService
            .GetUsersOnVacationAsync(_db, now, ct);
        var candidateQuery = CaseInactivityAutomationQuery.Build(
                _db,
                vacationUserIds,
                hasAvailableDefaultAssignee: true,
                cutoff,
                escalationCutoff,
                _db.ClientCases.ApplyClientCaseVisibility(access.Visibility))
            .Where(item => item.IsCandidate);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var rawTerm = search.Trim();
            rawTerm = rawTerm.Length > 120 ? rawTerm[..120] : rawTerm;
            var term = rawTerm.ToLower();
            candidateQuery = candidateQuery.Where(item =>
                item.Title.ToLower().Contains(term) ||
                item.Title.Contains(rawTerm) ||
                item.ClientName.ToLower().Contains(term) ||
                item.ClientName.Contains(rawTerm) ||
                item.LegalEntityName.ToLower().Contains(term) ||
                item.LegalEntityName.Contains(rawTerm) ||
                item.ResponsibleName.ToLower().Contains(term) ||
                item.ResponsibleName.Contains(rawTerm));
        }

        var prioritizedCandidateQuery = candidateQuery;

        if (normalizedAutomationState == "critical")
        {
            prioritizedCandidateQuery = prioritizedCandidateQuery.Where(item => item.IsCriticalCandidate);
        }

        var candidates = await prioritizedCandidateQuery
            .OrderBy(item => item.IsCriticalCandidate ? 0 : 1)
            .ThenBy(item => item.IsCriticalCandidate
                ? item.LastMeaningfulActivityAt
                : item.LastControlAnchorAt)
            .ThenBy(item => item.ClientName != string.Empty ? item.ClientName : item.LegalEntityName)
            .ThenBy(item => item.Title)
            .Take(effectiveLimit)
            .ToListAsync(ct);

        var created = new List<(TaskJournalItem Entity, TaskClientBinding Binding)>();
        var availableUsers = await LoadAvailableAutomationUsersAsync(
            candidates
                .Select(item => item.ResponsibleUserId)
                .Concat(new int?[]
                {
                    settings.CaseInactivityDefaultAssigneeUserId,
                    settings.CaseInactivityEscalationUserId,
                }),
            ct);
        var configuredDefaultAssignee = GetAvailableAutomationUser(
            availableUsers,
            settings.CaseInactivityDefaultAssigneeUserId);
        if (configuredDefaultAssignee is not null && vacationUserIds.Contains(configuredDefaultAssignee.Id))
        {
            configuredDefaultAssignee = null;
        }
        var escalationUser = GetAvailableAutomationUser(
            availableUsers,
            settings.CaseInactivityEscalationUserId);
        var assigneesByCaseId = candidates.ToDictionary(
            item => item.Id,
            item => BuildAutomationAssignee(
                item.ResponsibleUserId,
                availableUsers,
                configuredDefaultAssignee,
                access));
        var timeZonesByUserId = await CalendarTimeZoneHelper.ResolveUserTimeZonesAsync(
            _db,
            assigneesByCaseId.Values
                .Select(assignee => assignee.UserId ?? 0),
            ct);
        var candidateCaseIds = candidates
            .Select(item => item.Id)
            .ToArray();
        var activeTaskCaseIds = candidateCaseIds.Length == 0
            ? []
            : await _db.TaskJournalItems
                .AsNoTracking()
                .Where(task =>
                    task.ClientCaseId.HasValue &&
                    candidateCaseIds.Contains(task.ClientCaseId.Value) &&
                    ActiveTaskStatuses.Contains(task.Status))
                .Select(task => task.ClientCaseId!.Value)
                .Distinct()
                .ToHashSetAsync(ct);

        foreach (var item in candidates)
        {
            if (activeTaskCaseIds.Contains(item.Id))
            {
                continue;
            }

            var binding = BuildCaseBinding(item);
            if (binding.Error is not null)
            {
                continue;
            }

            var assignee = assigneesByCaseId[item.Id];
            var timeZone = assignee.UserId is int assigneeUserId && timeZonesByUserId.TryGetValue(assigneeUserId, out var resolvedTimeZone)
                ? resolvedTimeZone
                : CalendarTimeZoneHelper.ResolveTimeZone(null);
            var dueAt = BuildDefaultTaskDueAtUtc(now, timeZone);
            var entity = BuildAutomationTaskEntity(
                item,
                binding,
                access,
                assignee,
                settings,
                escalationUser,
                dueAt,
                now,
                item.LastTaskAt,
                item.LastTaskCommentAt,
                item.LastDocumentAt,
                item.LastCourtEventAt);
            AddTaskHistory(entity, access, "Задача создана автоматически из массового автоконтроля дел.");
            _db.TaskJournalItems.Add(entity);
            created.Add((entity, binding));
        }

        if (created.Count > 0)
        {
            await _db.SaveChangesAsync(ct);
            await _realtimePublisher.PublishTaskJournalChangedAsync(ct);
        }

        return Ok(new TaskJournalAutomationCreateResultDto
        {
            CreatedTasks = created.Select(item => Map(item.Entity, item.Binding)).ToList(),
            SkippedCount = candidates.Count - created.Count,
        });
    }

    [HttpGet("templates")]
    public async Task<ActionResult<IReadOnlyList<TaskJournalTemplateDto>>> GetTemplates(
        [FromQuery] string? search,
        [FromQuery] bool? active,
        [FromQuery] string? type,
        [FromQuery] string? priority,
        [FromQuery] string? bindingType,
        [FromQuery] int? assigneeUserId,
        CancellationToken ct)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin && access.Permission?.CanView != true)
        {
            return Forbid();
        }

        var query = _db.TaskJournalTemplates
            .Include(template => template.Participants)
            .AsQueryable();

        if (active.HasValue)
        {
            query = query.Where(template => template.IsActive == active.Value);
        }

        if (!string.IsNullOrWhiteSpace(type) && !string.Equals(type, "all", StringComparison.OrdinalIgnoreCase))
        {
            var normalizedType = NormalizeEnumValue(type, AllowedTypes, string.Empty);
            if (!string.IsNullOrWhiteSpace(normalizedType))
            {
                query = query.Where(template => template.Type == normalizedType);
            }
        }

        if (!string.IsNullOrWhiteSpace(priority) && !string.Equals(priority, "all", StringComparison.OrdinalIgnoreCase))
        {
            var normalizedPriority = NormalizeEnumValue(priority, AllowedPriorities, string.Empty);
            if (!string.IsNullOrWhiteSpace(normalizedPriority))
            {
                query = query.Where(template => template.Priority == normalizedPriority);
            }
        }

        if (!string.IsNullOrWhiteSpace(bindingType) && !string.Equals(bindingType, "all", StringComparison.OrdinalIgnoreCase))
        {
            var normalizedBindingType = NormalizeEnumValue(bindingType, AllowedTemplateBindingTypes, string.Empty);
            if (!string.IsNullOrWhiteSpace(normalizedBindingType))
            {
                query = query.Where(template => template.BindingType == normalizedBindingType);
            }
        }

        if (assigneeUserId is > 0)
        {
            query = query.Where(template => template.AssigneeUserId == assigneeUserId.Value);
        }

        var normalizedSearch = NormalizeOptional(search);
        if (normalizedSearch is not null)
        {
            var lowered = normalizedSearch.ToLower();
            query = query.Where(template =>
                template.Name.ToLower().Contains(lowered)
                || template.Title.ToLower().Contains(lowered)
                || (template.Description != null && template.Description.ToLower().Contains(lowered))
                || (template.TaskDescription != null && template.TaskDescription.ToLower().Contains(lowered))
                || template.TagsJson.ToLower().Contains(lowered)
                || template.BindingKey.ToLower().Contains(lowered));
        }

        var templates = await query
            .OrderByDescending(template => template.IsActive)
            .ThenBy(template => template.Name)
            .ThenBy(template => template.Id)
            .Take(200)
            .ToListAsync(ct);

        return Ok(templates.Select(MapTemplate).ToList());
    }

    [HttpGet("templates/{id:int}")]
    public async Task<ActionResult<TaskJournalTemplateDto>> GetTemplate(int id, CancellationToken ct)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin && access.Permission?.CanView != true)
        {
            return Forbid();
        }

        var template = await _db.TaskJournalTemplates
            .Include(item => item.Participants)
            .FirstOrDefaultAsync(item => item.Id == id, ct);

        return template is null ? NotFound() : Ok(MapTemplate(template));
    }

    [HttpPost("templates")]
    public async Task<ActionResult<TaskJournalTemplateDto>> CreateTemplate(
        [FromBody] UpsertTaskJournalTemplateRequest request,
        CancellationToken ct)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin && access.Permission?.CanCreate != true)
        {
            return Forbid();
        }

        var validation = ValidateTemplateRequest(request);
        if (validation is not null)
        {
            return BadRequest(new { message = validation });
        }

        var assignee = await ResolveTemplateAssigneeAsync(request, ct);
        if (assignee.Error is not null)
        {
            return BadRequest(new { message = assignee.Error });
        }

        var participants = await ResolveTemplateParticipantsAsync(request, ct);
        if (participants.Error is not null)
        {
            return BadRequest(new { message = participants.Error });
        }

        var now = DateTime.UtcNow;
        var entity = new TaskJournalTemplate
        {
            Name = request.Name.Trim(),
            Description = NormalizeOptional(request.Description),
            Title = request.Title.Trim(),
            TaskDescription = NormalizeOptional(request.TaskDescription),
            Type = NormalizeEnumValue(request.Type, AllowedTypes, "task"),
            Priority = NormalizeEnumValue(request.Priority, AllowedPriorities, "normal"),
            PlannedDurationMinutes = NormalizeTaskDuration(request.PlannedDurationMinutes),
            DueOffsetMinutes = NormalizeTemplateDueOffsetMinutes(request.DueOffsetMinutes),
            AssigneeUserId = assignee.UserId,
            AssigneeName = assignee.Name,
            BindingType = NormalizeEnumValue(request.BindingType, AllowedTemplateBindingTypes, "none"),
            BindingKey = NormalizeTemplateBindingKey(request.BindingType, request.BindingKey),
            IsActive = request.IsActive,
            RemindersJson = SerializeList(request.Reminders, AllowedReminders, ["atDue"]),
            ChannelsJson = SerializeList(request.Channels, AllowedChannels, ["inApp"]),
            TagsJson = SerializeFreeList(request.Tags),
            CreatedByUserId = access.UserId,
            CreatedByName = access.DisplayName,
            CreatedAt = now,
            UpdatedAt = now,
            Participants = participants.Items.Select(ToTemplateParticipantEntity).ToList(),
        };

        _db.TaskJournalTemplates.Add(entity);
        await _db.SaveChangesAsync(ct);
        return Created($"/api/tasks/templates/{entity.Id}", MapTemplate(entity));
    }

    [HttpPut("templates/{id:int}")]
    public async Task<ActionResult<TaskJournalTemplateDto>> UpdateTemplate(
        int id,
        [FromBody] UpsertTaskJournalTemplateRequest request,
        CancellationToken ct)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin && access.Permission?.CanEdit != true)
        {
            return Forbid();
        }

        var entity = await _db.TaskJournalTemplates
            .Include(item => item.Participants)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (entity is null)
        {
            return NotFound();
        }

        var validation = ValidateTemplateRequest(request);
        if (validation is not null)
        {
            return BadRequest(new { message = validation });
        }

        var assignee = await ResolveTemplateAssigneeAsync(request, ct);
        if (assignee.Error is not null)
        {
            return BadRequest(new { message = assignee.Error });
        }

        var participants = await ResolveTemplateParticipantsAsync(request, ct);
        if (participants.Error is not null)
        {
            return BadRequest(new { message = participants.Error });
        }

        entity.Name = request.Name.Trim();
        entity.Description = NormalizeOptional(request.Description);
        entity.Title = request.Title.Trim();
        entity.TaskDescription = NormalizeOptional(request.TaskDescription);
        entity.Type = NormalizeEnumValue(request.Type, AllowedTypes, "task");
        entity.Priority = NormalizeEnumValue(request.Priority, AllowedPriorities, "normal");
        entity.PlannedDurationMinutes = NormalizeTaskDuration(request.PlannedDurationMinutes);
        entity.DueOffsetMinutes = NormalizeTemplateDueOffsetMinutes(request.DueOffsetMinutes);
        entity.AssigneeUserId = assignee.UserId;
        entity.AssigneeName = assignee.Name;
        entity.BindingType = NormalizeEnumValue(request.BindingType, AllowedTemplateBindingTypes, "none");
        entity.BindingKey = NormalizeTemplateBindingKey(request.BindingType, request.BindingKey);
        entity.IsActive = request.IsActive;
        entity.RemindersJson = SerializeList(request.Reminders, AllowedReminders, ["atDue"]);
        entity.ChannelsJson = SerializeList(request.Channels, AllowedChannels, ["inApp"]);
        entity.TagsJson = SerializeFreeList(request.Tags);
        entity.UpdatedAt = DateTime.UtcNow;

        _db.TaskJournalTemplateParticipants.RemoveRange(entity.Participants);
        entity.Participants.Clear();
        foreach (var participant in participants.Items.Select(ToTemplateParticipantEntity))
        {
            entity.Participants.Add(participant);
        }

        await _db.SaveChangesAsync(ct);
        return Ok(MapTemplate(entity));
    }

    [HttpPatch("templates/{id:int}/active")]
    public async Task<ActionResult<TaskJournalTemplateDto>> UpdateTemplateActive(
        int id,
        [FromBody] UpdateTaskJournalTemplateActiveRequest request,
        CancellationToken ct)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin && access.Permission?.CanEdit != true)
        {
            return Forbid();
        }

        var entity = await _db.TaskJournalTemplates
            .Include(item => item.Participants)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (entity is null)
        {
            return NotFound();
        }

        entity.IsActive = request.IsActive;
        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(MapTemplate(entity));
    }

    [HttpDelete("templates/{id:int}")]
    public async Task<IActionResult> DeleteTemplate(int id, CancellationToken ct)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin && access.Permission?.CanDelete != true)
        {
            return Forbid();
        }

        var entity = await _db.TaskJournalTemplates.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (entity is null)
        {
            return NotFound();
        }

        _db.TaskJournalTemplates.Remove(entity);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<TaskJournalItemDto>> GetTask(int id, CancellationToken ct)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin && access.Permission?.CanView != true)
        {
            return Forbid();
        }

        var task = await ProjectTaskReadItems(BuildVisibleTaskBaseQuery(access)
            .AsNoTracking()
            .Where(item => item.Id == id))
            .FirstOrDefaultAsync(ct);
        return task is null ? NotFound() : Ok(Map(task));
    }

    [HttpGet("{id:int}/comments")]
    public async Task<ActionResult<IReadOnlyList<TaskJournalCommentDto>>> GetComments(int id, CancellationToken ct)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin && access.Permission?.CanView != true)
        {
            return Forbid();
        }

        var exists = await BuildVisibleTasksQuery(access).AnyAsync(item => item.Id == id, ct);
        if (!exists)
        {
            return NotFound();
        }

        var comments = await _db.TaskJournalComments
            .AsNoTracking()
            .Where(item => item.TaskJournalItemId == id)
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .Take(200)
            .ToListAsync(ct);

        return Ok(comments.Select(MapComment).ToList());
    }

    [HttpPost("{id:int}/comments")]
    public async Task<ActionResult<TaskJournalCommentDto>> CreateComment(int id, [FromBody] CreateTaskJournalCommentRequest request, CancellationToken ct)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin && access.Permission?.CanView != true)
        {
            return Forbid();
        }

        var entity = await BuildVisibleTasksQuery(access).FirstOrDefaultAsync(item => item.Id == id, ct);
        if (entity is null)
        {
            return NotFound();
        }

        if (!CanPostTaskComment(entity, access))
        {
            return Forbid();
        }

        var text = NormalizeOptional(request.Text);
        if (text is null)
        {
            return BadRequest(new { message = "Введите текст комментария." });
        }

        if (text.Length > 4000)
        {
            return BadRequest(new { message = "Комментарий не должен превышать 4000 символов." });
        }

        var comment = CreateTaskComment(entity.Id, access, "comment", text);
        comment.IsExternalCalendarVisible = request.IsExternalCalendarVisible;
        comment.ExternalSource = request.IsExternalCalendarVisible ? "crm" : null;
        comment.ExternalAuthorTrust = request.IsExternalCalendarVisible ? "verified-crm-user" : null;
        _db.TaskJournalComments.Add(comment);
        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _notificationService.NotifyTaskCommentAsync(entity, comment, ct);
        if (comment.IsExternalCalendarVisible)
        {
            await SafeSyncExternalCalendarTaskAsync(entity.Id, ct);
        }
        await _realtimePublisher.PublishTaskJournalChangedAsync(ct);

        return Created($"/api/tasks/{entity.Id}/comments/{comment.Id}", MapComment(comment));
    }

    [HttpPost("{id:int}/deadline-change-requests")]
    public async Task<ActionResult<TaskJournalCommentDto>> CreateDeadlineChangeRequest(
        int id,
        [FromBody] CreateTaskJournalDeadlineChangeRequest request,
        CancellationToken ct)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin && access.Permission?.CanView != true)
        {
            return Forbid();
        }

        var entity = await BuildVisibleTasksQuery(access).FirstOrDefaultAsync(item => item.Id == id, ct);
        if (entity is null)
        {
            return NotFound();
        }

        var settings = await GetTaskJournalSettingsAsync(ct);
        if (!settings.AllowExecutorDeadlineChangeRequests)
        {
            return BadRequest(new { message = "Запрос переноса срока отключен в настройках журнала задач." });
        }

        if (!IsTaskExecutor(entity, access.UserId) || IsTaskAuthor(entity, access.UserId))
        {
            return Forbid();
        }

        if (request.RequestedDueAt == default)
        {
            return BadRequest(new { message = "Укажите новый срок задачи." });
        }

        var reason = NormalizeOptional(request.Reason);
        if (reason is null)
        {
            return BadRequest(new { message = "Введите причину переноса срока задачи." });
        }

        if (reason.Length > 1000)
        {
            return BadRequest(new { message = "Причина переноса не должна превышать 1000 символов." });
        }

        var requestedDueAt = NormalizeUtcInstant(request.RequestedDueAt);
        var hasPendingDeadlineChangeRequest = await _db.TaskJournalDeadlineChangeRequests
            .AnyAsync(item => item.TaskJournalItemId == entity.Id && item.Status == "pending", ct);
        if (hasPendingDeadlineChangeRequest)
        {
            return BadRequest(new { message = "По этой задаче уже есть активный запрос переноса срока." });
        }

        var commentText = BuildDeadlineChangeRequestText(entity, access.DisplayName, requestedDueAt, reason);
        var comment = CreateTaskComment(
            entity.Id,
            access,
            "system",
            commentText,
            JsonSerializer.Serialize(new
            {
                type = "deadlineChangeRequest",
                requestedDueAt = ToIso(requestedDueAt),
                reason,
            }, JsonOptions));

        var deadlineRequest = new TaskJournalDeadlineChangeRequest
        {
            TaskJournalItemId = entity.Id,
            RequestedByUserId = access.UserId,
            RequestedByName = access.DisplayName,
            ReviewerUserId = entity.AuthorUserId,
            ReviewerName = entity.AuthorName,
            CurrentDueAt = entity.DueAt,
            RequestedDueAt = requestedDueAt,
            Reason = reason,
            Status = "pending",
            CreatedComment = comment,
            CreatedAt = DateTime.UtcNow,
        };

        _db.TaskJournalComments.Add(comment);
        _db.TaskJournalDeadlineChangeRequests.Add(deadlineRequest);
        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _notificationService.NotifyTaskDeadlineChangeRequestedAsync(entity, deadlineRequest, ct);
        await _realtimePublisher.PublishTaskJournalChangedAsync(ct);

        return Created($"/api/tasks/{entity.Id}/comments/{comment.Id}", MapComment(comment));
    }

    [HttpGet("{id:int}/deadline-change-requests")]
    public async Task<ActionResult<IReadOnlyList<TaskJournalDeadlineChangeRequestDto>>> GetDeadlineChangeRequests(int id, CancellationToken ct)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin && access.Permission?.CanView != true)
        {
            return Forbid();
        }

        var exists = await BuildVisibleTasksQuery(access).AnyAsync(item => item.Id == id, ct);
        if (!exists)
        {
            return NotFound();
        }

        var requests = await _db.TaskJournalDeadlineChangeRequests
            .AsNoTracking()
            .Where(item => item.TaskJournalItemId == id)
            .OrderBy(item => item.Status == "pending" ? 0 : 1)
            .ThenByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .ToListAsync(ct);

        return Ok(requests.Select(MapDeadlineChangeRequest).ToList());
    }

    [HttpGet("{id:int}/deadline-change-requests/{requestId:int}")]
    public async Task<ActionResult<TaskJournalDeadlineChangeRequestDto>> GetDeadlineChangeRequest(
        int id,
        int requestId,
        CancellationToken ct)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin && access.Permission?.CanView != true)
        {
            return Forbid();
        }

        var visibleTasks = BuildVisibleTaskBaseQuery(access);
        var request = await _db.TaskJournalDeadlineChangeRequests
            .AsNoTracking()
            .Where(item => item.Id == requestId
                && item.TaskJournalItemId == id
                && visibleTasks.Any(task => task.Id == item.TaskJournalItemId))
            .FirstOrDefaultAsync(ct);

        return request is null ? NotFound() : Ok(MapDeadlineChangeRequest(request));
    }

    [HttpPost("{id:int}/deadline-change-requests/{requestId:int}/approve")]
    public async Task<ActionResult<TaskJournalItemDto>> ApproveDeadlineChangeRequest(
        int id,
        int requestId,
        [FromBody] ApproveTaskJournalDeadlineChangeRequest request,
        CancellationToken ct)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin && access.Permission?.CanView != true)
        {
            return Forbid();
        }

        var entity = await BuildVisibleTasksQuery(access).FirstOrDefaultAsync(item => item.Id == id, ct);
        if (entity is null)
        {
            return NotFound(new { message = "Задача удалена или больше недоступна. Запрос переноса срока закрыт." });
        }

        var deadlineRequest = await _db.TaskJournalDeadlineChangeRequests
            .FirstOrDefaultAsync(item => item.Id == requestId && item.TaskJournalItemId == entity.Id, ct);
        if (deadlineRequest is null)
        {
            return NotFound(new { message = "Запрос переноса срока уже закрыт или больше недоступен." });
        }

        if (!string.Equals(deadlineRequest.Status, "pending", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { message = "Запрос переноса срока уже обработан." });
        }

        if (!access.IsAdmin && !IsTaskAuthor(entity, access.UserId))
        {
            return Forbid();
        }

        if (request.ApprovedDueAt == default)
        {
            return BadRequest(new { message = "Укажите новый срок задачи." });
        }

        var approvedDueAt = NormalizeUtcInstant(request.ApprovedDueAt);
        var decisionComment = NormalizeOptional(request.Comment);
        if (decisionComment is not null && decisionComment.Length > 1000)
        {
            return BadRequest(new { message = "Комментарий решения не должен превышать 1000 символов." });
        }

        var previousDueAt = entity.DueAt;
        var decidedAt = DateTime.UtcNow;
        entity.DueAt = approvedDueAt;
        entity.UpdatedAt = decidedAt;

        var decisionHistory = CreateTaskComment(
            entity.Id,
            access,
            "system",
            BuildDeadlineChangeApprovalText(access.DisplayName, previousDueAt, approvedDueAt, decisionComment),
            JsonSerializer.Serialize(new
            {
                type = "deadlineChangeApproved",
                deadlineChangeRequestId = deadlineRequest.Id,
                requestedByUserId = deadlineRequest.RequestedByUserId,
                requestedByName = deadlineRequest.RequestedByName,
                reviewerUserId = deadlineRequest.ReviewerUserId,
                reviewerName = deadlineRequest.ReviewerName,
                previousDueAt = ToIso(previousDueAt),
                requestedDueAt = ToIso(deadlineRequest.RequestedDueAt),
                approvedDueAt = ToIso(approvedDueAt),
                decisionComment,
                decidedAt = ToIso(decidedAt),
            }, JsonOptions));
        _db.TaskJournalComments.Add(decisionHistory);

        deadlineRequest.Status = "approved";
        deadlineRequest.DecisionDueAt = approvedDueAt;
        deadlineRequest.DecisionComment = decisionComment;
        deadlineRequest.DecidedByUserId = access.UserId;
        deadlineRequest.DecidedByName = access.DisplayName;
        deadlineRequest.DecidedAt = decidedAt;
        deadlineRequest.DecisionCommentEntry = decisionHistory;
        deadlineRequest.UpdatedAt = decidedAt;

        await _db.SaveChangesAsync(ct);
        await _notificationService.NotifyTaskDeadlineChangeApprovedAsync(entity, deadlineRequest, ct);
        await NotifyTaskScheduleConflictIfNeededAsync(entity, access, ct);
        await SafeSyncExternalCalendarTaskAsync(entity.Id, ct);
        await _realtimePublisher.PublishTaskJournalChangedAsync(ct);

        return Ok(Map(entity));
    }

    [HttpPost("{id:int}/deadline-change-requests/{requestId:int}/reject")]
    public async Task<ActionResult<TaskJournalItemDto>> RejectDeadlineChangeRequest(
        int id,
        int requestId,
        [FromBody] RejectTaskJournalDeadlineChangeRequest request,
        CancellationToken ct)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin && access.Permission?.CanView != true)
        {
            return Forbid();
        }

        var entity = await BuildVisibleTasksQuery(access).FirstOrDefaultAsync(item => item.Id == id, ct);
        if (entity is null)
        {
            return NotFound(new { message = "Задача удалена или больше недоступна. Запрос переноса срока закрыт." });
        }

        var deadlineRequest = await _db.TaskJournalDeadlineChangeRequests
            .FirstOrDefaultAsync(item => item.Id == requestId && item.TaskJournalItemId == entity.Id, ct);
        if (deadlineRequest is null)
        {
            return NotFound(new { message = "Запрос переноса срока уже закрыт или больше недоступен." });
        }

        if (!string.Equals(deadlineRequest.Status, "pending", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { message = "Запрос переноса срока уже обработан." });
        }

        if (!access.IsAdmin && !IsTaskAuthor(entity, access.UserId))
        {
            return Forbid();
        }

        var decisionComment = NormalizeOptional(request.Comment);
        if (decisionComment is null)
        {
            return BadRequest(new { message = "Укажите причину отказа в переносе срока." });
        }

        if (decisionComment.Length > 1000)
        {
            return BadRequest(new { message = "Комментарий решения не должен превышать 1000 символов." });
        }

        var decidedAt = DateTime.UtcNow;
        entity.UpdatedAt = decidedAt;

        var decisionHistory = CreateTaskComment(
            entity.Id,
            access,
            "system",
            BuildDeadlineChangeRejectionText(access.DisplayName, deadlineRequest.RequestedDueAt, decisionComment),
            JsonSerializer.Serialize(new
            {
                type = "deadlineChangeRejected",
                deadlineChangeRequestId = deadlineRequest.Id,
                requestedByUserId = deadlineRequest.RequestedByUserId,
                requestedByName = deadlineRequest.RequestedByName,
                reviewerUserId = deadlineRequest.ReviewerUserId,
                reviewerName = deadlineRequest.ReviewerName,
                currentDueAt = ToIso(entity.DueAt),
                requestedDueAt = ToIso(deadlineRequest.RequestedDueAt),
                decisionComment,
                decidedAt = ToIso(decidedAt),
            }, JsonOptions));
        _db.TaskJournalComments.Add(decisionHistory);

        deadlineRequest.Status = "rejected";
        deadlineRequest.DecisionComment = decisionComment;
        deadlineRequest.DecidedByUserId = access.UserId;
        deadlineRequest.DecidedByName = access.DisplayName;
        deadlineRequest.DecidedAt = decidedAt;
        deadlineRequest.DecisionCommentEntry = decisionHistory;
        deadlineRequest.UpdatedAt = decidedAt;

        await _db.SaveChangesAsync(ct);
        await _notificationService.NotifyTaskDeadlineChangeRejectedAsync(entity, deadlineRequest, ct);
        await _realtimePublisher.PublishTaskJournalChangedAsync(ct);

        return Ok(Map(entity));
    }

    [HttpGet("{id:int}/files")]
    public async Task<ActionResult<IReadOnlyList<TaskJournalFileDto>>> GetFiles(
        int id,
        CancellationToken ct,
        [FromQuery] bool latestOnly = false)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin && access.Permission?.CanView != true)
        {
            return Forbid();
        }

        var task = await BuildVisibleTasksQuery(access)
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (task is null)
        {
            return NotFound();
        }

        var filesQuery = _db.TaskJournalFiles
            .AsNoTracking()
            .Where(item => item.TaskJournalItemId == id);

        List<TaskJournalFile> files;
        if (!latestOnly)
        {
            var storedFiles = await filesQuery
                .OrderBy(item => item.OriginalFileName)
                .ThenByDescending(item => item.Version)
                .ThenByDescending(item => item.Id)
                .ToListAsync(ct);
            files = storedFiles
                .Where(IsTaskFileAvailable)
                .ToList();
        }
        else
        {
            var latestCandidates = await filesQuery
                .Where(item => !_db.TaskJournalFiles.Any(other =>
                    other.TaskJournalItemId == item.TaskJournalItemId
                    && other.DocumentKey == item.DocumentKey
                    && other.Version > item.Version))
                .ToListAsync(ct);

            files = [];
            var missingDocumentKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var candidate in latestCandidates)
            {
                if (IsTaskFileAvailable(candidate))
                {
                    files.Add(candidate);
                }
                else
                {
                    missingDocumentKeys.Add(candidate.DocumentKey);
                }
            }

            if (missingDocumentKeys.Count > 0)
            {
                var missingKeys = missingDocumentKeys.ToArray();
                var latestCandidateIds = latestCandidates.Select(item => item.Id).ToArray();
                var fallbackCandidates = await filesQuery
                    .Where(item => missingKeys.Contains(item.DocumentKey)
                        && !latestCandidateIds.Contains(item.Id))
                    .OrderBy(item => item.DocumentKey)
                    .ThenByDescending(item => item.Version)
                    .ThenByDescending(item => item.Id)
                    .ToListAsync(ct);

                var resolvedDocumentKeys = new HashSet<string>(
                    files.Select(item => item.DocumentKey),
                    StringComparer.Ordinal);
                foreach (var candidate in fallbackCandidates)
                {
                    if (!resolvedDocumentKeys.Contains(candidate.DocumentKey)
                        && IsTaskFileAvailable(candidate))
                    {
                        files.Add(candidate);
                        resolvedDocumentKeys.Add(candidate.DocumentKey);
                    }
                }
            }

            files = files
                .OrderBy(item => item.OriginalFileName)
                .ThenByDescending(item => item.Version)
                .ThenByDescending(item => item.Id)
                .ToList();
        }

        var caseDocumentIds = await ResolveTaskCaseDocumentIdsAsync(task.ClientCaseId, files, ct);
        return Ok(MapFiles(files, caseDocumentIds));
    }

    [HttpPost("{id:int}/files")]
    [RequestSizeLimit(MaxTaskFileBytes)]
    public async Task<ActionResult<TaskJournalFileDto>> UploadFile(
        int id,
        [FromForm] IFormFile? file,
        [FromForm] string? documentKey,
        CancellationToken ct)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin && access.Permission?.CanView != true)
        {
            return Forbid();
        }

        var entity = await BuildVisibleTasksQuery(access).FirstOrDefaultAsync(item => item.Id == id, ct);
        if (entity is null)
        {
            return NotFound();
        }

        if (!CanPostTaskComment(entity, access))
        {
            return Forbid();
        }

        var settings = await GetTaskJournalSettingsAsync(ct);
        if (!CanUploadTaskFile(entity, access, settings))
        {
            return Forbid();
        }

        var uploadError = UploadValidationHelper.ValidateRequiredFile(
            file,
            "Выберите файл для загрузки.",
            MaxTaskFileBytes,
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
            normalizedDocumentKey = Guid.NewGuid().ToString("N");
        }

        var latestVersion = isNewDocument
            ? 0
            : await _db.TaskJournalFiles
                .Where(item => item.TaskJournalItemId == id && item.DocumentKey == normalizedDocumentKey)
                .MaxAsync(item => (int?)item.Version, ct) ?? 0;

        if (!isNewDocument && latestVersion == 0)
        {
            return BadRequest(new { message = "Документ для новой версии не найден." });
        }

        var version = latestVersion + 1;
        var safeFileName = SanitizeFileName(uploadFile.FileName);
        var storedFileName = BuildStoredFileName(normalizedDocumentKey!, version, safeFileName);
        var relativePath = Path.Combine(id.ToString(), storedFileName);
        var fullPath = ResolveTaskFilePath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string? caseDocumentStoragePath = null;

        try
        {
            await using (var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await uploadFile.CopyToAsync(stream, ct);
            }

            var taskFile = new TaskJournalFile
            {
                TaskJournalItemId = id,
                DocumentKey = normalizedDocumentKey!,
                Version = version,
                OriginalFileName = safeFileName,
                StoredFileName = storedFileName,
                StoragePath = relativePath,
                ContentType = string.IsNullOrWhiteSpace(uploadFile.ContentType) ? "application/octet-stream" : uploadFile.ContentType,
                SizeBytes = uploadFile.Length,
                UploadedByUserId = access.UserId,
                UploadedByName = access.DisplayName,
                UploadedAt = DateTime.UtcNow,
            };

            _db.TaskJournalFiles.Add(taskFile);
            if (entity.ClientCaseId.HasValue)
            {
                var caseDocumentKey = BuildTaskCaseDocumentKey(entity.Id, normalizedDocumentKey!);
                var latestCaseVersion = await _db.ClientCaseDocuments
                    .Where(item => item.ClientCaseId == entity.ClientCaseId.Value && item.DocumentKey == caseDocumentKey)
                    .MaxAsync(item => (int?)item.Version, ct) ?? 0;
                var caseDocumentVersion = latestCaseVersion + 1;
                var caseStoredFileName = BuildStoredFileName(caseDocumentKey, caseDocumentVersion, safeFileName);
                caseDocumentStoragePath = _caseDocumentStorage.BuildRelativePath(entity.ClientCaseId.Value, caseStoredFileName);
                await using (var caseStream = uploadFile.OpenReadStream())
                {
                    await _caseDocumentStorage.SaveAsync(caseDocumentStoragePath, caseStream, ct);
                }

                var caseDocument = new ClientCaseDocument
                {
                    ClientCaseId = entity.ClientCaseId.Value,
                    DocumentKey = caseDocumentKey,
                    Version = caseDocumentVersion,
                    DocumentType = "Документ задачи",
                    Status = "Загружен",
                    OriginalFileName = safeFileName,
                    StoredFileName = caseStoredFileName,
                    StoragePath = caseDocumentStoragePath,
                    ContentType = taskFile.ContentType,
                    SizeBytes = taskFile.SizeBytes,
                    UploadedByUserId = taskFile.UploadedByUserId,
                    UploadedByName = taskFile.UploadedByName,
                    UploadedAt = taskFile.UploadedAt,
                };
                _db.ClientCaseDocuments.Add(caseDocument);
            }

            entity.UpdatedAt = DateTime.UtcNow;
            AddTaskHistory(
                entity,
                access,
                isNewDocument
                    ? $"Добавлен файл: {safeFileName}."
                    : $"Загружена новая версия файла: {safeFileName} (v{version}).",
                new { fileName = safeFileName, documentKey = normalizedDocumentKey, version });

            await _db.SaveChangesAsync(ct);
            await _realtimePublisher.PublishTaskJournalChangedAsync(ct);

            var caseDocumentIds = await ResolveTaskCaseDocumentIdsAsync(entity.ClientCaseId, [taskFile], ct);
            return Created($"/api/tasks/{entity.Id}/files/{taskFile.Id}", MapFile(
                taskFile,
                true,
                caseDocumentIds.GetValueOrDefault(BuildTaskCaseDocumentLookupKey(taskFile.DocumentKey, taskFile.Version))));
        }
        catch
        {
            TryDeletePhysicalFile(fullPath);
            if (caseDocumentStoragePath is not null)
            {
                _caseDocumentStorage.DeleteIfExists(caseDocumentStoragePath);
            }

            throw;
        }
    }

    [HttpGet("{id:int}/files/{fileId:int}/download")]
    public async Task<IActionResult> DownloadFile(int id, int fileId, CancellationToken ct)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin && access.Permission?.CanView != true)
        {
            return Forbid();
        }

        var exists = await BuildVisibleTasksQuery(access).AnyAsync(item => item.Id == id, ct);
        if (!exists)
        {
            return NotFound();
        }

        var taskFile = await _db.TaskJournalFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == fileId && item.TaskJournalItemId == id, ct);
        if (taskFile is null)
        {
            return NotFound();
        }

        var fullPath = ResolveTaskFilePath(taskFile.StoragePath);
        if (!System.IO.File.Exists(fullPath))
        {
            return NotFound(new { message = "Файл не найден в хранилище." });
        }

        return PhysicalFile(fullPath, taskFile.ContentType, taskFile.OriginalFileName);
    }

    [HttpDelete("{id:int}/files/{fileId:int}")]
    public async Task<IActionResult> DeleteFile(int id, int fileId, CancellationToken ct)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin && access.Permission?.CanView != true)
        {
            return Forbid();
        }

        var entity = await BuildVisibleTasksQuery(access).FirstOrDefaultAsync(item => item.Id == id, ct);
        if (entity is null)
        {
            return NotFound();
        }

        if (!CanPostTaskComment(entity, access))
        {
            return Forbid();
        }

        if (ShouldRestrictExecutorFullEdit(entity, access, await GetTaskJournalSettingsAsync(ct)))
        {
            return Forbid();
        }

        var taskFile = await _db.TaskJournalFiles
            .FirstOrDefaultAsync(item => item.Id == fileId && item.TaskJournalItemId == id, ct);
        if (taskFile is null)
        {
            return NotFound();
        }

        var fullPath = ResolveTaskFilePath(taskFile.StoragePath);
        _db.TaskJournalFiles.Remove(taskFile);
        entity.UpdatedAt = DateTime.UtcNow;
        AddTaskHistory(
            entity,
            access,
            $"Удален файл: {taskFile.OriginalFileName} (v{taskFile.Version}).",
            new { fileName = taskFile.OriginalFileName, taskFile.DocumentKey, taskFile.Version });
        await _db.SaveChangesAsync(ct);
        TryDeletePhysicalFile(fullPath);
        await _realtimePublisher.PublishTaskJournalChangedAsync(ct);

        return NoContent();
    }

    [HttpGet("billing-context")]
    public async Task<ActionResult<TaskBillingContextDto>> GetBillingContext(
        [FromQuery] int? clientId,
        [FromQuery] int? legalEntityId,
        [FromQuery] int clientCaseId,
        CancellationToken ct)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin && access.Permission?.CanCreate != true)
        {
            return Forbid();
        }

        if (clientCaseId <= 0 || (clientId is > 0) == (legalEntityId is > 0))
        {
            return BadRequest(new { message = "Укажите одного клиента или юридическое лицо и дело." });
        }

        var clientCase = await _db.ClientCases
            .AsNoTracking()
            .Where(item => item.Id == clientCaseId)
            .Select(item => new { item.Id, item.ClientId, item.LegalEntityId, item.Title })
            .FirstOrDefaultAsync(ct);
        if (clientCase is null
            || (clientId is > 0 && clientCase.ClientId != clientId)
            || (legalEntityId is > 0 && clientCase.LegalEntityId != legalEntityId))
        {
            return BadRequest(new { message = "Выбранное дело не относится к указанному клиенту." });
        }

        var today = DateTime.UtcNow.Date;
        var contractsQuery = _db.Contracts
            .AsNoTracking()
            .Where(contract => contract.Status != ContractStatus.Cancelled && contract.Status != ContractStatus.Terminated)
            .Where(contract => contract.Date <= today)
            .Where(contract => !contract.ValidUntil.HasValue || contract.ValidUntil.Value >= today);

        contractsQuery = clientId is > 0
            ? contractsQuery.Where(contract => contract.ClientContracts.Any(link => link.ClientId == clientId.Value))
            : contractsQuery.Where(contract => contract.LegalEntityContracts.Any(link => link.LegalEntityId == legalEntityId!.Value));

        var contracts = await contractsQuery
            .OrderByDescending(contract => contract.Status == ContractStatus.Concluded)
            .ThenByDescending(contract => contract.Date)
            .ThenByDescending(contract => contract.Id)
            .Select(contract => new TaskBillingContractOptionDto
            {
                Id = contract.Id,
                Number = contract.Number,
                Title = contract.Title ?? string.Empty,
                BillingMode = contract.BillingMode,
                Status = contract.Status,
                Amount = contract.Amount,
                Date = ToIso(contract.Date),
            })
            .ToListAsync(ct);

        return Ok(new TaskBillingContextDto
        {
            ClientId = clientId,
            LegalEntityId = legalEntityId,
            ClientCaseId = clientCase.Id,
            ClientCaseTitle = clientCase.Title,
            Contracts = contracts,
        });
    }

    [HttpPost]
    public async Task<ActionResult<TaskJournalItemDto>> CreateTask([FromBody] UpsertTaskJournalItemRequest request, CancellationToken ct)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin && access.Permission?.CanCreate != true)
        {
            return Forbid();
        }

        var validation = ValidateRequest(request);
        if (validation is not null)
        {
            return BadRequest(new { message = validation });
        }

        var binding = await ResolveClientBindingAsync(request, ct);
        if (binding.Error is not null)
        {
            return BadRequest(new { message = binding.Error });
        }

        var billing = await ResolveTaskBillingDecisionAsync(request, binding, ct);
        if (billing.Error is not null)
        {
            return BadRequest(new { message = billing.Error });
        }

        var participants = await ResolveParticipantsAsync(request, access.UserId, ct);
        if (participants.Error is not null)
        {
            return BadRequest(new { message = participants.Error });
        }

        var contractorPaymentValidation = await ValidateContractorPaymentRequestAsync(request, participants.PrimaryAssigneeUserId, ct);
        if (contractorPaymentValidation is not null)
        {
            return BadRequest(new { message = contractorPaymentValidation });
        }

        var nextStatus = NormalizeEnumValue(request.Status, AllowedStatuses, "new");
        var nextResult = NormalizeCompletionResult(nextStatus, request.Result);
        var authorApproverFallback = await AddAuthorAsApproverIfMissingAsync(
            participants,
            access.UserId,
            nextStatus,
            ct);
        participants = authorApproverFallback.Participants;
        var settings = await GetTaskJournalSettingsAsync(ct);
        var createAllowAdminForceCompletion = CanAdminForceCompleteTask(access, settings, nextStatus);
        var statusValidation = ValidateStatusRules(nextStatus, nextResult, participants.Items, settings.RequireCompletionResult, createAllowAdminForceCompletion);
        if (statusValidation is not null)
        {
            return BadRequest(new { message = statusValidation });
        }

        var createdAt = DateTime.UtcNow;
        var normalizedDueAt = NormalizeUtcInstant(request.DueAt);
        var approvalDueAt = ResolveApprovalDueAt(nextStatus, request.ApprovalDueAt, settings.ApprovalDueDays, createdAt);
        var revisionDueAtValidation = ValidateRevisionDueAt(nextStatus, normalizedDueAt, createdAt);
        if (revisionDueAtValidation is not null)
        {
            return BadRequest(new { message = revisionDueAtValidation });
        }

        var entity = new TaskJournalItem
        {
            ClientId = binding.ClientId,
            LegalEntityId = binding.LegalEntityId,
            ClientCaseId = binding.ClientCaseId,
            ClientBillingClassification = billing.Classification,
            BillingContractId = billing.ContractId,
            BillingModeSnapshot = billing.BillingMode,
            BillingDecisionAt = billing.DecisionAt,
            ObjectName = BuildObjectName(binding.OwnerName, binding.ClientCaseTitle),
            Title = request.Title.Trim(),
            Description = NormalizeOptional(request.Description),
            AssigneeUserId = participants.PrimaryAssigneeUserId,
            AssigneeName = participants.PrimaryAssigneeName,
            AuthorUserId = access.UserId,
            AuthorName = access.DisplayName,
            DueAt = normalizedDueAt,
            ApprovalDueAt = approvalDueAt,
            PlannedDurationMinutes = NormalizeTaskDuration(request.PlannedDurationMinutes),
            ContractorPaymentEnabled = request.ContractorPaymentEnabled,
            ContractorPaymentAmount = NormalizeMoneyAmount(request.ContractorPaymentAmount),
            ContractorPaymentDueDate = NormalizeDateOnly(request.ContractorPaymentDueDate),
            ContractorPaymentOnResult = request.ContractorPaymentOnResult,
            ContractorPaymentResultDelayDays = NormalizeContractorPaymentResultDelayDays(request.ContractorPaymentResultDelayDays),
            Type = NormalizeEnumValue(request.Type, AllowedTypes, "task"),
            Priority = NormalizeEnumValue(request.Priority, AllowedPriorities, "normal"),
            Status = nextStatus,
            IsUnderControl = request.IsUnderControl,
            ControlMarkedAt = request.IsUnderControl ? DateTime.UtcNow : null,
            ControlMarkedByUserId = request.IsUnderControl ? access.UserId : null,
            ControlMarkedByUserName = request.IsUnderControl ? access.DisplayName : string.Empty,
            Result = nextResult,
            RemindersJson = SerializeList(request.Reminders, AllowedReminders, ["atDue"]),
            ChannelsJson = SerializeList(request.Channels, AllowedChannels, ["inApp"]),
            TagsJson = SerializeFreeList(request.Tags),
            RecurrenceSequence = 0,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            Participants = participants.Items.Select(ToParticipantEntity).ToList(),
        };
        ApplyRecurrenceConfiguration(entity, request, normalizedDueAt > createdAt ? normalizedDueAt : createdAt);

        AddTaskHistory(entity, access, "Задача создана.");
        if (authorApproverFallback.Added)
        {
            AddTaskHistory(entity, access, "Отдельный согласующий не выбран: постановщик автоматически добавлен как согласующий.");
        }
        _db.TaskJournalItems.Add(entity);
        await _db.SaveChangesAsync(ct);
        await SyncContractorPaymentAsync(entity, await ResolveContractorPaymentResultBaseDateAsync(null, nextStatus, entity.CreatedAt, ct), ct);
        await _db.SaveChangesAsync(ct);
        await _notificationService.NotifyTaskAssignedAsync(entity, access.DisplayName, access.UserId, ct);
        if (IsApprovalActiveStatus(entity.Status))
        {
            await _notificationService.NotifyTaskApprovalRequestedAsync(entity, access.DisplayName, access.UserId, ct);
        }
        await NotifyTaskScheduleConflictIfNeededAsync(entity, access, ct);
        await SafeSyncExternalCalendarTaskAsync(entity.Id, ct);
        await _realtimePublisher.PublishTaskJournalChangedAsync(ct);
        return Created($"/api/tasks/{entity.Id}", Map(entity, binding));
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<TaskJournalItemDto>> UpdateTask(int id, [FromBody] UpsertTaskJournalItemRequest request, CancellationToken ct)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        var entity = await BuildVisibleTasksQuery(access).FirstOrDefaultAsync(item => item.Id == id, ct);
        if (entity is null)
        {
            return NotFound();
        }

        var settings = await GetTaskJournalSettingsAsync(ct);
        if (!CanFullyModifyTask(
                entity,
                access,
                access.Permission?.CanEdit == true,
                access.Permission?.CanEditOthers == true,
                settings))
        {
            return Forbid();
        }

        var validation = ValidateRequest(request);
        if (validation is not null)
        {
            return BadRequest(new { message = validation });
        }

        var binding = await ResolveClientBindingAsync(request, ct);
        if (binding.Error is not null)
        {
            return BadRequest(new { message = binding.Error });
        }

        TaskBillingDecision? newBindingBilling = null;
        if (!entity.ClientId.HasValue && !entity.LegalEntityId.HasValue
            && (binding.ClientId.HasValue || binding.LegalEntityId.HasValue))
        {
            if (!TaskClientBillingClassification.IsDecision(request.ClientBillingClassification))
            {
                return BadRequest(new { message = "При привязке клиента укажите, оплачивается ли задача." });
            }

            newBindingBilling = await ResolveTaskBillingDecisionAsync(request, binding, ct);
            if (newBindingBilling.Error is not null)
            {
                return BadRequest(new { message = newBindingBilling.Error });
            }
        }

        var participants = await ResolveParticipantsAsync(request, entity.AssigneeUserId ?? access.UserId, ct);
        if (participants.Error is not null)
        {
            return BadRequest(new { message = participants.Error });
        }

        var previousAuthorUserId = entity.AuthorUserId;
        var nextAuthorUserId = entity.AuthorUserId;
        var nextAuthorName = entity.AuthorName;
        var authorChanged = request.AuthorUserId.HasValue && request.AuthorUserId != entity.AuthorUserId;
        if (authorChanged)
        {
            if (!access.IsAdmin)
            {
                return Forbid();
            }

            var nextAuthor = await _db.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(user =>
                    user.Id == request.AuthorUserId!.Value
                    && user.IsActive
                    && user.IsApproved,
                    ct);
            if (nextAuthor is null)
            {
                return BadRequest(new { message = "Выбранный постановщик не найден или неактивен." });
            }

            nextAuthorUserId = nextAuthor.Id;
            nextAuthorName = BuildUserDisplayName(nextAuthor.FullName, nextAuthor.Email);
        }

        var contractorPaymentValidation = await ValidateContractorPaymentRequestAsync(request, participants.PrimaryAssigneeUserId, ct);
        if (contractorPaymentValidation is not null)
        {
            return BadRequest(new { message = contractorPaymentValidation });
        }

        var previousStatus = entity.Status;
        var nextStatus = NormalizeEnumValue(request.Status, AllowedStatuses, "new");
        if (IsCompletionStatus(nextStatus)
            && !string.Equals(previousStatus, nextStatus, StringComparison.OrdinalIgnoreCase)
            && !CanCompleteTask(entity, access, settings))
        {
            return Forbid();
        }
        var authorApproverAdded = false;
        if (!IsApprovalActiveStatus(previousStatus))
        {
            var authorApproverFallback = await AddAuthorAsApproverIfMissingAsync(
                participants,
                nextAuthorUserId,
                nextStatus,
                ct);
            participants = authorApproverFallback.Participants;
            authorApproverAdded = authorApproverFallback.Added;
        }
        var approvalQueueRestarted = ShouldRestartApprovalQueue(previousStatus, nextStatus);
        var approvalEndedBecauseNoApprovers = IsApprovalActiveStatus(previousStatus)
            && !participants.Items.Any(participant =>
                string.Equals(participant.Role, "approver", StringComparison.OrdinalIgnoreCase))
            && (IsApprovalActiveStatus(nextStatus)
                || string.Equals(nextStatus, "inProgress", StringComparison.OrdinalIgnoreCase));
        if (approvalEndedBecauseNoApprovers && IsApprovalActiveStatus(nextStatus))
        {
            nextStatus = "inProgress";
            approvalQueueRestarted = false;
        }
        participants = ResetApprovalQueueIfStartingApproval(previousStatus, nextStatus, participants);
        var nextResult = NormalizeCompletionResult(nextStatus, request.Result);
        var updateAllowAdminForceCompletion = CanAdminForceCompleteTask(access, settings, nextStatus);
        var statusValidation = ValidateStatusRules(nextStatus, nextResult, participants.Items, settings.RequireCompletionResult, updateAllowAdminForceCompletion);
        if (statusValidation is not null)
        {
            return BadRequest(new { message = statusValidation });
        }

        var becameDone = !string.Equals(entity.Status, "done", StringComparison.OrdinalIgnoreCase)
            && string.Equals(nextStatus, "done", StringComparison.OrdinalIgnoreCase);
        var becameCancelled = !string.Equals(entity.Status, "cancelled", StringComparison.OrdinalIgnoreCase)
            && string.Equals(nextStatus, "cancelled", StringComparison.OrdinalIgnoreCase);
        var requestDueAt = NormalizeUtcInstant(request.DueAt);
        var revisionDueAtValidation = ValidateRevisionDueAt(nextStatus, requestDueAt, DateTime.UtcNow);
        if (revisionDueAtValidation is not null)
        {
            return BadRequest(new { message = revisionDueAtValidation });
        }

        var previousAssigneeUserId = entity.AssigneeUserId;
        var previousAssigneeName = entity.AssigneeName;
        var changeLabels = BuildTaskChangeLabels(entity, request, binding, participants, nextStatus).ToList();
        if (newBindingBilling is not null)
        {
            changeLabels.Add(newBindingBilling.Classification == TaskClientBillingClassification.Billable
                ? "Оплата клиента: платная задача"
                : "Оплата клиента: неоплачиваемая задача");
        }
        AddIfChanged(changeLabels, "Постановщик", entity.AuthorName, nextAuthorName);
        var assigneeChanged = HasTaskAssigneeChange(changeLabels);
        IReadOnlyCollection<int>? formerAuthorRecipientIds = authorChanged && previousAuthorUserId.HasValue
            ? [previousAuthorUserId.Value]
            : null;
        var changeComment = NormalizeOptional(request.ChangeComment);
        var updatedAt = DateTime.UtcNow;
        var approvalDueAt = approvalEndedBecauseNoApprovers
            ? null
            : ResolveApprovalDueAt(nextStatus, request.ApprovalDueAt, settings.ApprovalDueDays, updatedAt);

        entity.ClientId = binding.ClientId;
        entity.LegalEntityId = binding.LegalEntityId;
        entity.ClientCaseId = binding.ClientCaseId;
        if (newBindingBilling is not null)
        {
            entity.ClientBillingClassification = newBindingBilling.Classification;
            entity.BillingContractId = newBindingBilling.ContractId;
            entity.BillingModeSnapshot = newBindingBilling.BillingMode;
            entity.BillingDecisionAt = updatedAt;
        }
        entity.ObjectName = BuildObjectName(binding.OwnerName, binding.ClientCaseTitle);
        entity.Title = request.Title.Trim();
        entity.Description = NormalizeOptional(request.Description);
        entity.AssigneeUserId = participants.PrimaryAssigneeUserId;
        entity.AssigneeName = participants.PrimaryAssigneeName;
        entity.AuthorUserId = nextAuthorUserId;
        entity.AuthorName = nextAuthorName;
        entity.DueAt = requestDueAt;
        entity.ApprovalDueAt = approvalDueAt;
        entity.PlannedDurationMinutes = NormalizeTaskDuration(request.PlannedDurationMinutes);
        entity.ContractorPaymentEnabled = request.ContractorPaymentEnabled;
        entity.ContractorPaymentAmount = NormalizeMoneyAmount(request.ContractorPaymentAmount);
        entity.ContractorPaymentDueDate = NormalizeDateOnly(request.ContractorPaymentDueDate);
        entity.ContractorPaymentOnResult = request.ContractorPaymentOnResult;
        entity.ContractorPaymentResultDelayDays = NormalizeContractorPaymentResultDelayDays(request.ContractorPaymentResultDelayDays);
        entity.Type = NormalizeEnumValue(request.Type, AllowedTypes, "task");
        entity.Priority = NormalizeEnumValue(request.Priority, AllowedPriorities, "normal");
        entity.Status = nextStatus;
        ApplyTaskControlState(entity, request.IsUnderControl, access);
        entity.Result = nextResult;
        entity.RemindersJson = SerializeList(request.Reminders, AllowedReminders, ["atDue"]);
        entity.ChannelsJson = SerializeList(request.Channels, AllowedChannels, ["inApp"]);
        entity.TagsJson = SerializeFreeList(request.Tags);
        entity.UpdatedAt = updatedAt;
        ApplyRecurrenceConfiguration(entity, request, requestDueAt > updatedAt ? requestDueAt : updatedAt);

        _db.TaskJournalParticipants.RemoveRange(entity.Participants);
        entity.Participants.Clear();
        foreach (var participant in participants.Items.Select(ToParticipantEntity))
        {
            entity.Participants.Add(participant);
        }

        if (authorChanged)
        {
            var pendingDeadlineRequests = await _db.TaskJournalDeadlineChangeRequests
                .Where(item => item.TaskJournalItemId == entity.Id && item.Status == "pending")
                .ToListAsync(ct);
            foreach (var deadlineRequest in pendingDeadlineRequests)
            {
                deadlineRequest.ReviewerUserId = nextAuthorUserId;
                deadlineRequest.ReviewerName = nextAuthorName;
                deadlineRequest.UpdatedAt = updatedAt;
            }
        }

        if (changeLabels.Count > 0)
        {
            AddTaskHistory(entity, access, $"Изменены поля: {string.Join(", ", changeLabels)}.");
        }

        if (approvalQueueRestarted)
        {
            AddTaskHistory(entity, access, "Очередь согласования запущена заново: все согласующие снова ожидают решения.");
        }
        if (authorApproverAdded)
        {
            AddTaskHistory(entity, access, "Отдельный согласующий не выбран: постановщик автоматически добавлен как согласующий.");
        }
        else if (approvalEndedBecauseNoApprovers)
        {
            AddTaskHistory(entity, access, "Последний согласующий удалён: задача автоматически возвращена в работу.");
        }

        TaskJournalComment? userComment = null;
        if (changeComment is not null)
        {
            userComment = CreateTaskComment(entity.Id, access, "comment", changeComment);
            _db.TaskJournalComments.Add(userComment);
        }

        var nextRecurringTask = becameDone
            ? await CreateNextRecurringTaskIfNeededAsync(entity, access, ct)
            : null;
        await SyncContractorPaymentAsync(entity, await ResolveContractorPaymentResultBaseDateAsync(previousStatus, nextStatus, entity.UpdatedAt, ct), ct);
        await _db.SaveChangesAsync(ct);
        await SynchronizeExistingTaskChatMembersAsync([entity], ct);
        await AppendTaskChatSystemMessagesAsync([(entity.Id, (IReadOnlyList<string>)changeLabels)], access, ct);
        await NotifyRecurringTaskCreatedAsync(nextRecurringTask, access, ct);
        if (changeLabels.Count > 0)
        {
            if (becameCancelled)
            {
                await _notificationService.NotifyTaskCancelledAsync(entity, access.DisplayName, access.UserId, "Отменена", ct);
            }
            else if (assigneeChanged)
            {
                await _notificationService.NotifyTaskReassignedAsync(entity, previousAssigneeUserId, previousAssigneeName, changeLabels, access.DisplayName, access.UserId, changeComment, ct, formerAuthorRecipientIds);
            }
            else
            {
                await _notificationService.NotifyTaskChangedAsync(entity, changeLabels, access.DisplayName, access.UserId, changeComment, ct, formerAuthorRecipientIds);
            }
        }
        else if (userComment is not null)
        {
            await _notificationService.NotifyTaskCommentAsync(entity, userComment, ct);
        }

        if (approvalQueueRestarted || (!IsApprovalActiveStatus(previousStatus) && IsApprovalActiveStatus(entity.Status)))
        {
            await _notificationService.NotifyTaskApprovalRequestedAsync(entity, access.DisplayName, access.UserId, ct);
        }

        if (becameDone)
        {
            await _notificationService.NotifyTaskCompletedAsync(entity, ct);
        }
        await NotifyTaskScheduleConflictIfNeededAsync(entity, access, ct);
        await SafeSyncExternalCalendarTaskAsync(entity.Id, ct);
        await _realtimePublisher.PublishTaskJournalChangedAsync(ct);
        return Ok(Map(entity, binding));
    }

    [HttpPatch("{id:int}/status")]
    public async Task<ActionResult<TaskJournalItemDto>> UpdateStatus(int id, [FromBody] UpdateTaskJournalStatusRequest request, CancellationToken ct)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin && access.Permission?.CanView != true)
        {
            return Forbid();
        }

        var entity = await BuildVisibleTasksQuery(access).FirstOrDefaultAsync(item => item.Id == id, ct);
        if (entity is null)
        {
            return NotFound();
        }

        var settings = await GetTaskJournalSettingsAsync(ct);
        var nextStatus = NormalizeEnumValue(request.Status, AllowedStatuses, "new");
        if (!CanUpdateTaskStatus(entity, access, settings, nextStatus))
        {
            return Forbid();
        }

        var requestedResult = NormalizeOptional(request.Result);
        var nextResult = NormalizeCompletionResult(nextStatus, requestedResult ?? entity.Result);
        var authorApproverAdded = await AddAuthorAsApproverIfMissingAsync(entity, nextStatus, ct);

        var allowAdminForceCompletion = CanAdminForceCompleteTask(access, settings, nextStatus);
        var statusValidation = ValidateStatusRules(nextStatus, nextResult, entity.Participants, settings.RequireCompletionResult, allowAdminForceCompletion);
        if (statusValidation is not null)
        {
            return BadRequest(new { message = statusValidation });
        }

        var approvalQueueRestarted = ResetApprovalQueueIfStartingApproval(entity.Status, nextStatus, entity.Participants);
        var becameDone = !string.Equals(entity.Status, "done", StringComparison.OrdinalIgnoreCase)
            && string.Equals(nextStatus, "done", StringComparison.OrdinalIgnoreCase);
        var becameCancelled = !string.Equals(entity.Status, "cancelled", StringComparison.OrdinalIgnoreCase)
            && string.Equals(nextStatus, "cancelled", StringComparison.OrdinalIgnoreCase);
        var oldStatus = entity.Status;
        var oldResult = entity.Result;
        var oldDueAt = entity.DueAt;
        var requestedDueAt = request.DueAt.HasValue ? NormalizeUtcInstant(request.DueAt.Value) : entity.DueAt;
        var revisionDueAtValidation = ValidateRevisionDueAt(nextStatus, requestedDueAt, DateTime.UtcNow);
        if (revisionDueAtValidation is not null)
        {
            return BadRequest(new { message = revisionDueAtValidation });
        }

        entity.Status = nextStatus;
        entity.DueAt = requestedDueAt;
        if (!IsApprovalActiveStatus(oldStatus) && IsApprovalActiveStatus(nextStatus) && entity.ApprovalDueAt is null)
        {
            entity.ApprovalDueAt = ResolveApprovalDueAt(nextStatus, null, settings.ApprovalDueDays, DateTime.UtcNow);
        }
        if (requestedResult is not null
            || (string.Equals(nextStatus, "done", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(entity.Result)))
        {
            entity.Result = nextResult;
        }
        entity.UpdatedAt = DateTime.UtcNow;

        var statusChanged = !string.Equals(oldStatus, entity.Status, StringComparison.OrdinalIgnoreCase);
        var resultChanged = !string.Equals(NormalizeOptional(oldResult), NormalizeOptional(entity.Result), StringComparison.Ordinal);
        var dueAtChanged = oldDueAt != entity.DueAt;
        if (statusChanged || resultChanged || dueAtChanged)
        {
            var parts = new List<string>();
            if (statusChanged)
            {
                parts.Add($"статус: {TaskStatusLabel(oldStatus)} → {TaskStatusLabel(entity.Status)}");
            }

            if (resultChanged)
            {
                parts.Add("результат обновлён");
            }

            if (dueAtChanged)
            {
                parts.Add($"срок: {FormatHistoryDateTime(oldDueAt)} → {FormatHistoryDateTime(entity.DueAt)}");
            }

            if (approvalQueueRestarted)
            {
                parts.Add("очередь согласования запущена заново");
            }

            if (authorApproverAdded)
            {
                parts.Add("постановщик добавлен как согласующий");
            }

            AddTaskHistory(entity, access, $"Изменено: {string.Join(", ", parts)}.");
        }

        var nextRecurringTask = becameDone
            ? await CreateNextRecurringTaskIfNeededAsync(entity, access, ct)
            : null;
        await SyncContractorPaymentAsync(entity, await ResolveContractorPaymentResultBaseDateAsync(oldStatus, entity.Status, entity.UpdatedAt, ct), ct);
        await _db.SaveChangesAsync(ct);
        await SynchronizeExistingTaskChatMembersAsync([entity], ct);
        await NotifyRecurringTaskCreatedAsync(nextRecurringTask, access, ct);
        if (statusChanged || resultChanged || dueAtChanged)
        {
            var changeLabels = new List<string>();
            if (statusChanged)
            {
                changeLabels.Add($"Статус: {TaskStatusLabel(oldStatus)} -> {TaskStatusLabel(entity.Status)}");
            }

            if (resultChanged)
            {
                changeLabels.Add($"Результат: {FormatChangeValue(oldResult)} -> {FormatChangeValue(entity.Result)}");
            }

            if (dueAtChanged)
            {
                changeLabels.Add($"Срок: {FormatHistoryDateTime(oldDueAt)} -> {FormatHistoryDateTime(entity.DueAt)}");
            }

            await AppendTaskChatSystemMessagesAsync([(entity.Id, (IReadOnlyList<string>)changeLabels)], access, ct);

            if (approvalQueueRestarted || (!IsApprovalActiveStatus(oldStatus) && IsApprovalActiveStatus(entity.Status)))
            {
                await _notificationService.NotifyTaskApprovalRequestedAsync(entity, access.DisplayName, access.UserId, ct);
            }
            else
            {
                if (becameCancelled)
                {
                    await _notificationService.NotifyTaskCancelledAsync(entity, access.DisplayName, access.UserId, "Отменена", ct);
                }
                else
                {
                    await _notificationService.NotifyTaskChangedAsync(entity, changeLabels, access.DisplayName, access.UserId, null, ct);
                }
            }
        }

        if (becameDone)
        {
            await _notificationService.NotifyTaskCompletedAsync(entity, ct);
        }
        await SafeSyncExternalCalendarTaskAsync(entity.Id, ct);
        await _realtimePublisher.PublishTaskJournalChangedAsync(ct);
        return Ok(Map(entity));
    }

    [HttpPatch("{id:int}/control")]
    public async Task<ActionResult<TaskJournalItemDto>> UpdateControl(int id, [FromBody] UpdateTaskJournalControlRequest request, CancellationToken ct)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin
            && access.Permission?.CanEdit != true
            && access.Permission?.CanEditOthers != true)
        {
            return Forbid();
        }

        var entity = await BuildVisibleTasksQuery(access).FirstOrDefaultAsync(item => item.Id == id, ct);
        if (entity is null)
        {
            return NotFound();
        }

        var settings = await GetTaskJournalSettingsAsync(ct);
        if (!CanFullyModifyTask(
                entity,
                access,
                access.Permission?.CanEdit == true,
                access.Permission?.CanEditOthers == true,
                settings))
        {
            return Forbid();
        }

        var changed = ApplyTaskControlState(entity, request.IsUnderControl, access);
        var observerAdded = false;
        if (request.IsUnderControl && !IsTaskAuthor(entity, access.UserId) && !IsTaskExecutor(entity, access.UserId))
        {
            observerAdded = await EnsureTaskObserverAsync(entity, access.UserId, ct);
        }

        if (!changed && !observerAdded)
        {
            return Ok(Map(entity));
        }

        entity.UpdatedAt = DateTime.UtcNow;
        AddTaskHistory(
            entity,
            access,
            request.IsUnderControl
                ? "Задача поставлена на контроль."
                : "Задача снята с контроля.",
            new { request.IsUnderControl, observerAdded });
        await _db.SaveChangesAsync(ct);

        await _notificationService.NotifyTaskChangedAsync(
            entity,
            [request.IsUnderControl ? "контроль: включен" : "контроль: выключен"],
            access.DisplayName,
            access.UserId,
            null,
            ct);
        await _realtimePublisher.PublishTaskJournalChangedAsync(ct);
        return Ok(Map(entity));
    }

    [HttpPost("bulk")]
    public async Task<ActionResult<object>> BulkUpdateTasks([FromBody] BulkUpdateTaskJournalItemsRequest request, CancellationToken ct)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin && access.Permission?.CanEdit != true)
        {
            return Forbid();
        }

        var ids = request.Ids
            .Where(id => id > 0)
            .Distinct()
            .Take(500)
            .ToList();
        if (ids.Count == 0)
        {
            return BadRequest(new { message = "Выберите задачи для массового изменения." });
        }

        var hasAssigneeChange = request.AssigneeUserId.HasValue;
        var hasPriorityChange = !string.IsNullOrWhiteSpace(request.Priority);
        var hasStatusChange = !string.IsNullOrWhiteSpace(request.Status);
        var hasDueAtChange = request.DueAt.HasValue;
        if (!hasAssigneeChange && !hasPriorityChange && !hasStatusChange && !hasDueAtChange)
        {
            return BadRequest(new { message = "Укажите хотя бы одно массовое изменение." });
        }

        User? assignee = null;
        if (hasAssigneeChange)
        {
            assignee = await _db.Users
                .FirstOrDefaultAsync(item => item.Id == request.AssigneeUserId!.Value && item.IsActive && item.IsApproved, ct);
            if (assignee is null)
            {
                return BadRequest(new { message = "Указанный исполнитель не найден." });
            }
        }

        var nextPriority = hasPriorityChange
            ? NormalizeEnumValue(request.Priority, AllowedPriorities, string.Empty)
            : null;
        if (hasPriorityChange && string.IsNullOrWhiteSpace(nextPriority))
        {
            return BadRequest(new { message = "Некорректный приоритет задачи." });
        }

        var nextStatus = hasStatusChange
            ? NormalizeEnumValue(request.Status, AllowedStatuses, string.Empty)
            : null;
        if (hasStatusChange && string.IsNullOrWhiteSpace(nextStatus))
        {
            return BadRequest(new { message = "Некорректный статус задачи." });
        }

        if (string.Equals(nextStatus, "approved", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { message = "Массовое согласование задач запрещено. Согласующий должен принимать решение по каждой задаче отдельно." });
        }

        if (hasDueAtChange && request.DueAt == default)
        {
            return BadRequest(new { message = "Укажите корректный срок задачи." });
        }

        var settings = await GetTaskJournalSettingsAsync(ct);
        var entities = await BuildVisibleTasksQuery(access)
            .Where(task => ids.Contains(task.Id))
            .ToListAsync(ct);

        var updated = 0;
        var notifications = new List<(TaskJournalItem Task, IReadOnlyList<string> ChangeLabels, bool BecameDone, bool BecameCancelled, bool ApprovalRequested, bool AssigneeChanged, int? PreviousAssigneeUserId, string? PreviousAssigneeName)>();
        var now = DateTime.UtcNow;
        foreach (var entity in entities)
        {
            var requiresFullEdit = hasAssigneeChange
                || hasPriorityChange
                || hasDueAtChange
                || (hasStatusChange && !IsCompletionStatus(nextStatus));
            if (requiresFullEdit
                && !CanFullyModifyTask(
                    entity,
                    access,
                    access.Permission?.CanEdit == true,
                    access.Permission?.CanEditOthers == true,
                    settings))
            {
                continue;
            }

            if (nextStatus is not null && !CanUpdateTaskStatus(entity, access, settings, nextStatus))
            {
                continue;
            }

            var changes = new List<string>();
            var approvalRequested = false;
            var previousAssigneeUserId = entity.AssigneeUserId;
            var previousAssigneeName = entity.AssigneeName;
            var assigneeChanged = false;

            if (assignee is not null && ReplacePrimaryAssignee(entity, assignee, now))
            {
                assigneeChanged = true;
                changes.Add($"Исполнитель: {FormatChangeValue(previousAssigneeName)} -> {FormatChangeValue(entity.AssigneeName)}");
            }

            if (nextPriority is not null && !string.Equals(entity.Priority, nextPriority, StringComparison.OrdinalIgnoreCase))
            {
                var oldPriority = entity.Priority;
                entity.Priority = nextPriority;
                changes.Add($"Приоритет: {TaskPriorityLabel(oldPriority)} -> {TaskPriorityLabel(entity.Priority)}");
            }

            var requestDueAt = request.DueAt.HasValue ? NormalizeUtcInstant(request.DueAt.Value) : (DateTime?)null;
            if (requestDueAt.HasValue && entity.DueAt != requestDueAt.Value)
            {
                changes.Add($"Срок: {FormatHistoryDateTime(entity.DueAt)} -> {FormatHistoryDateTime(requestDueAt.Value)}");
                entity.DueAt = requestDueAt.Value;
            }

            var becameDone = false;
            var becameCancelled = false;
            if (nextStatus is not null)
            {
                var oldStatus = entity.Status;
                var oldResult = entity.Result;
                var requestedResult = NormalizeOptional(request.Result);
                var nextResult = NormalizeCompletionResult(nextStatus, requestedResult ?? entity.Result);
                var effectiveDueAt = requestDueAt ?? entity.DueAt;
                if (ValidateRevisionDueAt(nextStatus, effectiveDueAt, now) is not null)
                {
                    continue;
                }

                var bulkAllowAdminForceCompletion = CanAdminForceCompleteTask(access, settings, nextStatus);
                var statusValidation = ValidateStatusRules(nextStatus, nextResult, entity.Participants, settings.RequireCompletionResult, bulkAllowAdminForceCompletion);
                if (statusValidation is not null)
                {
                    continue;
                }

                var approvalQueueRestarted = ResetApprovalQueueIfStartingApproval(entity.Status, nextStatus, entity.Participants);
                becameDone = !string.Equals(entity.Status, "done", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(nextStatus, "done", StringComparison.OrdinalIgnoreCase);
                becameCancelled = !string.Equals(entity.Status, "cancelled", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(nextStatus, "cancelled", StringComparison.OrdinalIgnoreCase);
                entity.Status = nextStatus;
                approvalRequested = approvalQueueRestarted
                    || (!IsApprovalActiveStatus(oldStatus) && IsApprovalActiveStatus(entity.Status));
                if (approvalRequested && entity.ApprovalDueAt is null)
                {
                    var oldApprovalDueAt = entity.ApprovalDueAt;
                    entity.ApprovalDueAt = ResolveApprovalDueAt(nextStatus, null, settings.ApprovalDueDays, now);
                    changes.Add($"Срок согласования: {FormatOptionalDateTime(oldApprovalDueAt)} -> {FormatOptionalDateTime(entity.ApprovalDueAt)}");
                }
                if (requestedResult is not null
                    || (string.Equals(nextStatus, "done", StringComparison.OrdinalIgnoreCase)
                        && string.IsNullOrWhiteSpace(oldResult)))
                {
                    entity.Result = nextResult;
                }

                if (!string.Equals(oldStatus, entity.Status, StringComparison.OrdinalIgnoreCase))
                {
                    changes.Add($"Статус: {TaskStatusLabel(oldStatus)} -> {TaskStatusLabel(entity.Status)}");
                }

                if (!string.Equals(NormalizeOptional(oldResult), NormalizeOptional(entity.Result), StringComparison.Ordinal))
                {
                    changes.Add($"Результат: {FormatChangeValue(oldResult)} -> {FormatChangeValue(entity.Result)}");
                }

                if (approvalQueueRestarted)
                {
                    changes.Add("Очередь согласования: запущена заново");
                }
            }

            if (changes.Count == 0)
            {
                continue;
            }

            entity.UpdatedAt = now;
            AddTaskHistory(entity, access, $"Массовое изменение: {string.Join(", ", changes)}.");
            updated++;
            notifications.Add((entity, changes, becameDone, becameCancelled, approvalRequested, assigneeChanged, previousAssigneeUserId, previousAssigneeName));
        }

        await _db.SaveChangesAsync(ct);
        await SynchronizeExistingTaskChatMembersAsync(notifications.Select(item => item.Task).ToList(), ct);
        await AppendTaskChatSystemMessagesAsync(
            notifications.Select(item => (item.Task.Id, item.ChangeLabels)).ToList(),
            access,
            ct);
        foreach (var notification in notifications)
        {
            if (notification.BecameCancelled)
            {
                await _notificationService.NotifyTaskCancelledAsync(
                    notification.Task,
                    access.DisplayName,
                    access.UserId,
                    "Отменена",
                    ct);
            }
            else if (notification.AssigneeChanged)
            {
                await _notificationService.NotifyTaskReassignedAsync(
                    notification.Task,
                    notification.PreviousAssigneeUserId,
                    notification.PreviousAssigneeName,
                    notification.ChangeLabels,
                    access.DisplayName,
                    access.UserId,
                    null,
                    ct);
            }
            else
            {
                await _notificationService.NotifyTaskChangedAsync(
                    notification.Task,
                    notification.ChangeLabels,
                    access.DisplayName,
                    access.UserId,
                    null,
                    ct);
            }

            if (notification.ApprovalRequested)
            {
                await _notificationService.NotifyTaskApprovalRequestedAsync(notification.Task, access.DisplayName, access.UserId, ct);
            }

            if (notification.BecameDone)
            {
                await _notificationService.NotifyTaskCompletedAsync(notification.Task, ct);
            }

            await NotifyTaskScheduleConflictIfNeededAsync(notification.Task, access, ct);
            await SafeSyncExternalCalendarTaskAsync(notification.Task.Id, ct);
        }
        if (updated > 0)
        {
            await _realtimePublisher.PublishTaskJournalChangedAsync(ct);
        }

        return Ok(new
        {
            requested = ids.Count,
            updated,
            skipped = ids.Count - updated,
        });
    }

    [HttpPost("bulk/delete")]
    public async Task<ActionResult<object>> BulkDeleteTasks([FromBody] BulkDeleteTaskJournalItemsRequest request, CancellationToken ct)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin
            && access.Permission?.CanDelete != true
            && access.Permission?.CanDeleteOthers != true)
        {
            return Forbid();
        }

        var ids = request.Ids
            .Where(id => id > 0)
            .Distinct()
            .Take(500)
            .ToList();
        if (ids.Count == 0)
        {
            return BadRequest(new { message = "Выберите задачи для массового удаления." });
        }

        var settings = await GetTaskJournalSettingsAsync(ct);
        var entities = await BuildVisibleTasksQuery(access)
            .Where(task => ids.Contains(task.Id))
            .ToListAsync(ct);

        var toDelete = new List<TaskJournalItem>();
        foreach (var entity in entities)
        {
            if (!CanDeleteTask(entity, access, settings))
            {
                continue;
            }

            if (_externalCalendarSyncCoordinator is not null)
            {
                try
                {
                    await _externalCalendarSyncCoordinator.DeleteTaskForConnectedUsersAsync(entity.Id, cancellationToken: ct);
                }
                catch (InvalidOperationException ex) when (ex.Message.StartsWith("ExternalCalendarTaskDeleteFailed", StringComparison.Ordinal))
                {
                    return Problem(
                        title: "ExternalCalendarTaskDeleteFailed",
                        detail: "Не удалось удалить связанное событие из внешних календарей. Задачи не удалены, проверьте журналы синхронизации календарей и повторите операцию.",
                        statusCode: 409);
                }
            }

            toDelete.Add(entity);
        }

        foreach (var entity in toDelete)
        {
            await _notificationService.NotifyTaskCancelledAsync(entity, access.DisplayName, access.UserId, "Удалена", ct);
            _db.TaskJournalItems.Remove(entity);
        }

        if (toDelete.Count > 0)
        {
            await _db.SaveChangesAsync(ct);
            await _realtimePublisher.PublishTaskJournalChangedAsync(ct);
        }

        return Ok(new
        {
            requested = ids.Count,
            updated = toDelete.Count,
            skipped = ids.Count - toDelete.Count,
        });
    }

    [HttpPatch("{id:int}/approval")]
    public async Task<ActionResult<TaskJournalItemDto>> UpdateApproval(int id, [FromBody] UpdateTaskJournalApprovalRequest request, CancellationToken ct)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin && access.Permission?.CanView != true)
        {
            return Forbid();
        }

        var entity = await BuildVisibleTasksQuery(access).FirstOrDefaultAsync(item => item.Id == id, ct);
        if (entity is null)
        {
            return NotFound();
        }

        var decision = NormalizeEnumValue(request.Decision, AllowedApprovalDecisions, string.Empty);
        if (string.IsNullOrWhiteSpace(decision))
        {
            return BadRequest(new { message = "Укажите решение по согласованию." });
        }

        var approvers = entity.Participants
            .Where(participant => string.Equals(participant.Role, "approver", StringComparison.OrdinalIgnoreCase))
            .OrderBy(participant => participant.SortOrder)
            .ThenBy(participant => participant.Id)
            .ToList();
        if (approvers.Count == 0)
        {
            return BadRequest(new { message = "У задачи нет согласующих." });
        }

        if (!IsApprovalActiveStatus(entity.Status))
        {
            return BadRequest(new { message = "Задача не находится на согласовании." });
        }

        var participant = request.ParticipantId.HasValue
            ? approvers.FirstOrDefault(item => item.Id == request.ParticipantId.Value)
            : approvers.FirstOrDefault(item => item.UserId == access.UserId);
        if (participant is null && access.IsAdmin)
        {
            participant = approvers.FirstOrDefault(item =>
                !string.Equals(item.ApprovalStatus, "approved", StringComparison.OrdinalIgnoreCase));
        }

        if (participant is null)
        {
            return BadRequest(new { message = "Вы не указаны согласующим по этой задаче." });
        }

        if (!access.IsAdmin && participant.UserId != access.UserId)
        {
            return Forbid();
        }

        var participantIndex = approvers.IndexOf(participant);
        if (participantIndex > 0 && approvers.Take(participantIndex).Any(item =>
                !string.Equals(item.ApprovalStatus, "approved", StringComparison.OrdinalIgnoreCase)))
        {
            return BadRequest(new { message = "Сначала решение должен принять предыдущий согласующий." });
        }

        var oldStatus = entity.Status;
        var resolution = NormalizeOptional(request.Resolution);
        var isRevisionRequested = string.Equals(decision, "revisionRequested", StringComparison.OrdinalIgnoreCase);
        if (isRevisionRequested && string.IsNullOrWhiteSpace(resolution))
        {
            return BadRequest(new { message = "Укажите рецензию: почему задача возвращается на доработку и какие поправки нужно внести." });
        }

        DateTime? revisionDueAt = null;
        if (isRevisionRequested && request.RevisionDueAt.HasValue && request.RevisionDueAt.Value != default)
        {
            revisionDueAt = NormalizeUtcInstant(request.RevisionDueAt.Value);
        }

        var finalAction = NormalizeApprovalFinalAction(request.FinalAction);
        var completesTaskAfterApproval = string.Equals(finalAction, "complete", StringComparison.OrdinalIgnoreCase);
        var isFinalApprovalDecision = string.Equals(decision, "approved", StringComparison.OrdinalIgnoreCase)
            && approvers.All(item => ReferenceEquals(item, participant)
                || string.Equals(item.ApprovalStatus, "approved", StringComparison.OrdinalIgnoreCase));
        participant.ApprovalStatus = decision;
        participant.Resolution = resolution;
        participant.RespondedAt = DateTime.UtcNow;
        participant.UpdatedAt = DateTime.UtcNow;

        if (isRevisionRequested)
        {
            entity.Status = "revision";
            if (revisionDueAt.HasValue)
            {
                entity.DueAt = revisionDueAt.Value;
            }
        }
        else if (approvers.All(item => string.Equals(item.ApprovalStatus, "approved", StringComparison.OrdinalIgnoreCase)))
        {
            entity.Status = isFinalApprovalDecision && completesTaskAfterApproval ? "done" : "approved";
            if (string.Equals(entity.Status, "done", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(entity.Result))
            {
                entity.Result = "Задача выполнена.";
            }
        }
        else if (!string.Equals(entity.Status, "overdueApproval", StringComparison.OrdinalIgnoreCase))
        {
            entity.Status = "onApproval";
        }

        entity.UpdatedAt = DateTime.UtcNow;
        AddTaskHistory(
            entity,
            access,
            BuildApprovalHistoryText(decision, participant.DisplayName, resolution, entity.Result),
            new
            {
                kind = "approvalDecision",
                actorUserId = access.UserId,
                actorName = access.DisplayName,
                participantId = participant.Id,
                approverUserId = participant.UserId,
                approverName = participant.DisplayName,
                decision,
                resolution,
                taskResult = NormalizeOptional(entity.Result),
                resultingStatus = entity.Status,
                finalAction,
                revisionDueAt,
                approvalDueAt = entity.ApprovalDueAt,
                respondedAt = participant.RespondedAt
            });
        var nextRecurringTask = !string.Equals(oldStatus, "done", StringComparison.OrdinalIgnoreCase)
            && string.Equals(entity.Status, "done", StringComparison.OrdinalIgnoreCase)
            ? await CreateNextRecurringTaskIfNeededAsync(entity, access, ct)
            : null;
        await SyncContractorPaymentAsync(entity, await ResolveContractorPaymentResultBaseDateAsync(oldStatus, entity.Status, entity.UpdatedAt, ct), ct);
        await _db.SaveChangesAsync(ct);
        await NotifyRecurringTaskCreatedAsync(nextRecurringTask, access, ct);
        await _notificationService.NotifyTaskApprovalDecisionAsync(
            entity,
            participant,
            decision,
            access.DisplayName,
            access.UserId,
            resolution,
            ct);
        if (string.Equals(entity.Status, "done", StringComparison.OrdinalIgnoreCase))
        {
            await _notificationService.NotifyTaskCompletedAsync(entity, ct);
        }
        await SafeSyncExternalCalendarTaskAsync(entity.Id, ct);
        await _realtimePublisher.PublishTaskJournalChangedAsync(ct);
        return Ok(Map(entity));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteTask(int id, CancellationToken ct)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin
            && access.Permission?.CanDelete != true
            && access.Permission?.CanDeleteOthers != true)
        {
            return Forbid();
        }

        var entity = await BuildVisibleTasksQuery(access).FirstOrDefaultAsync(item => item.Id == id, ct);
        if (entity is null)
        {
            return NotFound();
        }

        if (!CanDeleteTask(entity, access, await GetTaskJournalSettingsAsync(ct)))
        {
            return Forbid();
        }

        if (_externalCalendarSyncCoordinator is not null)
        {
            try
            {
                await _externalCalendarSyncCoordinator.DeleteTaskForConnectedUsersAsync(entity.Id, cancellationToken: ct);
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("ExternalCalendarTaskDeleteFailed", StringComparison.Ordinal))
            {
                return Problem(
                    title: "ExternalCalendarTaskDeleteFailed",
                    detail: "Не удалось удалить связанное событие из внешних календарей. Задача не удалена, проверьте журналы синхронизации календарей и повторите операцию.",
                    statusCode: 409);
            }
        }

        await _notificationService.NotifyTaskCancelledAsync(entity, access.DisplayName, access.UserId, "Удалена", ct);
        _db.TaskJournalItems.Remove(entity);
        await _db.SaveChangesAsync(ct);
        await _realtimePublisher.PublishTaskJournalChangedAsync(ct);
        return NoContent();
    }

    private async Task NotifyTaskScheduleConflictIfNeededAsync(TaskJournalItem entity, TaskAccess access, CancellationToken ct)
    {
        if (!ActiveTaskStatuses.Contains(entity.Status, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        var conflicts = await FindTaskScheduleConflictsAsync(entity, ct);
        if (conflicts.Count == 0)
        {
            return;
        }

        await _notificationService.NotifyTaskScheduleConflictAsync(entity, conflicts, access.DisplayName, access.UserId, ct);
    }

    private async Task SafeSyncExternalCalendarTaskAsync(int taskId, CancellationToken ct)
    {
        if (_externalCalendarTaskSyncOutbox is not null)
        {
            try
            {
                await _externalCalendarTaskSyncOutbox.EnqueueAsync(taskId, cancellationToken: ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"[ExternalCalendar] Task sync queue failed for task {taskId}: {ex.Message}");
            }
            return;
        }

        if (_externalCalendarSyncCoordinator is null)
        {
            return;
        }

        try
        {
            await _externalCalendarSyncCoordinator.SyncTaskForConnectedUsersAsync(taskId, cancellationToken: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[ExternalCalendar] Task sync failed for task {taskId}: {ex.Message}");
        }
    }

    private async Task<IReadOnlyList<TaskJournalItem>> FindTaskScheduleConflictsAsync(TaskJournalItem entity, CancellationToken ct)
    {
        var assigneeUserIds = GetTaskAssigneeUserIds(entity);
        var assigneeNames = GetTaskAssigneeNames(entity);
        if (assigneeUserIds.Count == 0 && assigneeNames.Count == 0)
        {
            return Array.Empty<TaskJournalItem>();
        }

        var start = entity.DueAt;
        var end = GetTaskEndAt(entity);
        var windowStart = start.Date.AddDays(-1);
        var windowEnd = end.Date.AddDays(1);

        var candidates = await _db.TaskJournalItems
            .Include(task => task.Participants)
            .Where(task =>
                task.Id != entity.Id &&
                ActiveTaskStatuses.Contains(task.Status) &&
                task.DueAt >= windowStart &&
                task.DueAt < windowEnd)
            .ToListAsync(ct);

        return candidates
            .Where(candidate =>
                SharesTaskAssignee(candidate, assigneeUserIds, assigneeNames) &&
                IntervalsOverlap(start, end, candidate.DueAt, GetTaskEndAt(candidate)))
            .OrderBy(candidate => candidate.DueAt)
            .ThenBy(candidate => candidate.Id)
            .ToList();
    }

    private static IReadOnlyList<int> GetTaskAssigneeUserIds(TaskJournalItem entity)
    {
        var ids = new HashSet<int>();
        if (entity.AssigneeUserId is { } assigneeUserId && assigneeUserId > 0)
        {
            ids.Add(assigneeUserId);
        }

        foreach (var participant in entity.Participants)
        {
            if (participant.Role == "assignee" && participant.UserId is { } userId && userId > 0)
            {
                ids.Add(userId);
            }
        }

        return ids.ToList();
    }

    private static IReadOnlyList<string> GetTaskAssigneeNames(TaskJournalItem entity)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(entity.AssigneeName))
        {
            names.Add(entity.AssigneeName.Trim().ToLowerInvariant());
        }

        foreach (var participant in entity.Participants)
        {
            if (participant.Role == "assignee" && !string.IsNullOrWhiteSpace(participant.DisplayName))
            {
                names.Add(participant.DisplayName.Trim().ToLowerInvariant());
            }
        }

        return names.ToList();
    }

    private static bool SharesTaskAssignee(
        TaskJournalItem candidate,
        IReadOnlyCollection<int> assigneeUserIds,
        IReadOnlyCollection<string> assigneeNames)
    {
        if (candidate.AssigneeUserId is { } assigneeUserId &&
            assigneeUserId > 0 &&
            assigneeUserIds.Contains(assigneeUserId))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(candidate.AssigneeName) &&
            assigneeNames.Contains(candidate.AssigneeName.Trim().ToLowerInvariant()))
        {
            return true;
        }

        return candidate.Participants.Any(participant =>
            participant.Role == "assignee" &&
            ((participant.UserId is { } participantUserId &&
              participantUserId > 0 &&
              assigneeUserIds.Contains(participantUserId)) ||
             (!string.IsNullOrWhiteSpace(participant.DisplayName) &&
              assigneeNames.Contains(participant.DisplayName.Trim().ToLowerInvariant()))));
    }

    private static DateTime GetTaskEndAt(TaskJournalItem entity)
        => entity.DueAt.AddMinutes(NormalizeTaskDuration(entity.PlannedDurationMinutes));

    private static bool IntervalsOverlap(DateTime firstStart, DateTime firstEnd, DateTime secondStart, DateTime secondEnd)
        => firstStart < secondEnd && secondStart < firstEnd;

    /// <summary>
    /// Собирает единый запрос списка и сводки задач. Все ограничения применяются
    /// до Count/Skip/Take, чтобы API не читал скрытые или отфильтрованные строки
    /// и не заставлял frontend последовательно выгружать журнал целиком.
    /// </summary>
    private IQueryable<TaskJournalItem> BuildTaskJournalListQuery(
        TaskAccess access,
        string? search,
        string? status,
        string? type,
        int? assigneeUserId,
        DateTime? from,
        DateTime? to,
        int? clientCaseId,
        int? clientId,
        int? legalEntityId,
        string? priority,
        bool? underControl,
        int? authorUserId,
        string? participantRole,
        bool approvalScope,
        string? metric,
        DateTime? todayFrom,
        DateTime? todayTo,
        string? billingStatus = null)
    {
        var normalizedBillingStatus = billingStatus?.Trim().ToLowerInvariant();
        var effectiveStatus = normalizedBillingStatus == "unbilled" ? null : status;
        var query = ApplyTaskJournalFilters(
            BuildVisibleTaskBaseQuery(access).AsNoTracking(),
            search,
            effectiveStatus,
            type,
            assigneeUserId,
            from,
            to,
            clientCaseId,
            clientId,
            legalEntityId,
            priority,
            underControl,
            authorUserId,
            participantRole,
            access.UserId);

        query = ApplyTaskApprovalScope(query, access, approvalScope);
        query = ApplyTaskMetricFilter(query, metric, todayFrom, todayTo);
        return ApplyTaskBillingFilter(query, billingStatus);
    }

    private static IQueryable<TaskJournalItem> ApplyTaskBillingFilter(
        IQueryable<TaskJournalItem> query,
        string? billingStatus)
    {
        var normalized = billingStatus?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return query;
        }

        if (normalized is not ("unbilled" or "fixed" or "invoiced" or "internal" or "legacy"))
        {
            return query;
        }

        return normalized switch
        {
            "unbilled" => query.Where(task =>
                task.Status != "cancelled" &&
                task.ClientBillingClassification == TaskClientBillingClassification.Billable &&
                !task.BillingInvoiceId.HasValue &&
                (task.BillingModeSnapshot == null || task.BillingModeSnapshot.ToLower() != ContractBillingMode.Fixed.ToLower())),
            "fixed" => query.Where(task =>
                task.Status != "done" && task.Status != "cancelled" &&
                task.ClientBillingClassification == TaskClientBillingClassification.Billable &&
                !task.BillingInvoiceId.HasValue &&
                task.BillingModeSnapshot != null &&
                task.BillingModeSnapshot.ToLower() == ContractBillingMode.Fixed.ToLower()),
            "invoiced" => query.Where(task =>
                task.Status != "done" && task.Status != "cancelled" &&
                task.ClientBillingClassification == TaskClientBillingClassification.Billable && task.BillingInvoiceId.HasValue),
            "internal" => query.Where(task =>
                task.Status != "done" && task.Status != "cancelled" &&
                task.ClientBillingClassification == TaskClientBillingClassification.Internal),
            "legacy" => query.Where(task =>
                task.Status != "done" && task.Status != "cancelled" &&
                task.ClientBillingClassification == TaskClientBillingClassification.Legacy),
            _ => query,
        };
    }

    /// <summary>
    /// Ограничивает режим «Согласования» только задачами, по которым текущий
    /// пользователь действительно принимает следующее решение, либо рассматривает
    /// запрос переноса срока. Администратор видит все активные решения.
    /// </summary>
    private static IQueryable<TaskJournalItem> ApplyTaskApprovalScope(
        IQueryable<TaskJournalItem> query,
        TaskAccess access,
        bool approvalScope)
    {
        if (!approvalScope)
        {
            return query;
        }

        if (access.IsAdmin)
        {
            return query.Where(task =>
                (task.Status == "onApproval" || task.Status == "overdueApproval") ||
                task.DeadlineChangeRequests.Any(request => request.Status == "pending"));
        }

        var userId = access.UserId;
        return query.Where(task =>
            task.DeadlineChangeRequests.Any(request =>
                request.Status == "pending" && request.ReviewerUserId == userId) ||
            ((task.Status == "onApproval" || task.Status == "overdueApproval") &&
             task.Participants.Any(participant =>
                 participant.Role == "approver" &&
                 participant.UserId == userId &&
                 participant.ApprovalStatus != "approved" &&
                 !task.Participants.Any(previous =>
                     previous.Role == "approver" &&
                     previous.ApprovalStatus != "approved" &&
                     (previous.SortOrder < participant.SortOrder ||
                      (previous.SortOrder == participant.SortOrder && previous.Id < participant.Id))))));
    }

    /// <summary>
    /// Применяет выбранную карточку-метрику как SQL-фильтр. Границы «Сегодня»
    /// приходят от frontend в часовом поясе пользователя и сравниваются как
    /// полуинтервал [начало; начало следующего дня).
    /// </summary>
    private static IQueryable<TaskJournalItem> ApplyTaskMetricFilter(
        IQueryable<TaskJournalItem> query,
        string? metric,
        DateTime? todayFrom,
        DateTime? todayTo)
    {
        var normalizedMetric = metric?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedMetric) ||
            string.Equals(normalizedMetric, "none", StringComparison.OrdinalIgnoreCase))
        {
            return query;
        }

        var now = DateTime.UtcNow;
        if (string.Equals(normalizedMetric, "active", StringComparison.OrdinalIgnoreCase))
        {
            return query.Where(task => task.Status != "done" && task.Status != "cancelled");
        }

        if (string.Equals(normalizedMetric, "overdue", StringComparison.OrdinalIgnoreCase))
        {
            return query.Where(task =>
                task.Status != "done" &&
                task.Status != "cancelled" &&
                task.DueAt < now);
        }

        if (string.Equals(normalizedMetric, "today", StringComparison.OrdinalIgnoreCase))
        {
            var from = todayFrom ?? now.Date;
            var to = todayTo is { } requestedTo && requestedTo > from ? requestedTo : from.AddDays(1);
            return query.Where(task =>
                task.Status != "done" &&
                task.Status != "cancelled" &&
                task.DueAt >= from &&
                task.DueAt < to);
        }

        if (string.Equals(normalizedMetric, "highPriority", StringComparison.OrdinalIgnoreCase))
        {
            return query.Where(task =>
                task.Status != "done" &&
                task.Status != "cancelled" &&
                (task.Priority == "high" || task.Priority == "urgent"));
        }

        return query;
    }

    private static IQueryable<TaskJournalItem> ApplyTaskJournalFilters(
        IQueryable<TaskJournalItem> query,
        string? search,
        string? status,
        string? type,
        int? assigneeUserId,
        DateTime? from,
        DateTime? to,
        int? clientCaseId,
        int? clientId,
        int? legalEntityId,
        string? priority = null,
        bool? underControl = null,
        int? authorUserId = null,
        string? participantRole = null,
        int? participantUserId = null)
    {
        if (!string.IsNullOrWhiteSpace(status) && !string.Equals(status, "all", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(status, "active", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(task => task.Status != "done" && task.Status != "cancelled");
            }
            else if (string.Equals(status, "overdue", StringComparison.OrdinalIgnoreCase))
            {
                var now = DateTime.UtcNow;
                query = query.Where(task =>
                    task.Status != "done" &&
                    task.Status != "cancelled" &&
                    task.DueAt < now);
            }
            else
            {
                var normalizedStatus = NormalizeEnumValue(status, AllowedStatuses, "new");
                query = query.Where(task => task.Status == normalizedStatus);
            }
        }

        if (!string.IsNullOrWhiteSpace(type) && !string.Equals(type, "all", StringComparison.OrdinalIgnoreCase))
        {
            var normalizedType = NormalizeEnumValue(type, AllowedTypes, "task");
            query = query.Where(task => task.Type == normalizedType);
        }

        if (!string.IsNullOrWhiteSpace(priority) && !string.Equals(priority, "all", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(priority, "highPriority", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(task => task.Priority == "high" || task.Priority == "urgent");
            }
            else
            {
                var normalizedPriority = NormalizeEnumValue(priority, AllowedPriorities, "normal");
                query = query.Where(task => task.Priority == normalizedPriority);
            }
        }

        if (underControl.HasValue)
        {
            query = query.Where(task => task.IsUnderControl == underControl.Value);
        }

        if (assigneeUserId.HasValue)
        {
            query = query.Where(task =>
                task.AssigneeUserId == assigneeUserId.Value ||
                task.Participants.Any(participant =>
                    participant.Role == "assignee" &&
                    participant.UserId == assigneeUserId.Value));
        }

        if (authorUserId.HasValue)
        {
            query = query.Where(task => task.AuthorUserId == authorUserId.Value);
        }

        var normalizedParticipantRole = participantRole?.Trim().ToLowerInvariant();
        if (participantUserId.HasValue && normalizedParticipantRole is "observer" or "approver")
        {
            query = query.Where(task => task.Participants.Any(participant =>
                participant.Role == normalizedParticipantRole &&
                participant.UserId == participantUserId.Value));
        }

        if (clientCaseId.HasValue)
        {
            query = query.Where(task => task.ClientCaseId == clientCaseId.Value);
        }

        if (clientId.HasValue)
        {
            query = query.Where(task => task.ClientId == clientId.Value);
        }

        if (legalEntityId.HasValue)
        {
            query = query.Where(task => task.LegalEntityId == legalEntityId.Value);
        }

        if (from.HasValue)
        {
            query = query.Where(task => task.DueAt >= from.Value);
        }

        if (to.HasValue)
        {
            query = query.Where(task => task.DueAt <= to.Value);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var rawTerm = search.Trim();
            var term = rawTerm.ToLower();
            query = query.Where(task =>
                task.ObjectName.ToLower().Contains(term) ||
                task.ObjectName.Contains(rawTerm) ||
                (task.Client != null && (
                    task.Client.Name.ToLower().Contains(term) ||
                    task.Client.Name.Contains(rawTerm))) ||
                (task.LegalEntity != null && (
                    task.LegalEntity.ShortName.ToLower().Contains(term) ||
                    task.LegalEntity.ShortName.Contains(rawTerm) ||
                    (task.LegalEntity.FullName ?? "").ToLower().Contains(term) ||
                    (task.LegalEntity.FullName ?? "").Contains(rawTerm) ||
                    (task.LegalEntity.Inn ?? "").ToLower().Contains(term) ||
                    (task.LegalEntity.Inn ?? "").Contains(rawTerm))) ||
                (task.ClientCase != null && (
                    task.ClientCase.Title.ToLower().Contains(term) ||
                    task.ClientCase.Title.Contains(rawTerm))) ||
                task.Title.ToLower().Contains(term) ||
                task.Title.Contains(rawTerm));
        }

        return query;
    }

    private static IQueryable<TaskJournalItem> ApplyTaskJournalSort(
        IQueryable<TaskJournalItem> query,
        string? sortBy,
        string? sortDir)
    {
        var desc = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase);
        return (sortBy?.Trim().ToLowerInvariant()) switch
        {
            "counterparty" or "client" => desc
                ? query.OrderByDescending(task =>
                        (task.Client != null ? task.Client.Name : null) ??
                        (task.LegalEntity != null ? task.LegalEntity.ShortName ?? task.LegalEntity.FullName : null) ??
                        task.ObjectName)
                    .ThenBy(task => task.ClientCase != null ? task.ClientCase.Title : string.Empty)
                    .ThenBy(task => task.Id)
                : query.OrderBy(task =>
                        (task.Client != null ? task.Client.Name : null) ??
                        (task.LegalEntity != null ? task.LegalEntity.ShortName ?? task.LegalEntity.FullName : null) ??
                        task.ObjectName)
                    .ThenBy(task => task.ClientCase != null ? task.ClientCase.Title : string.Empty)
                    .ThenBy(task => task.Id),
            "title" => desc
                ? query.OrderByDescending(task => task.Title).ThenBy(task => task.Id)
                : query.OrderBy(task => task.Title).ThenBy(task => task.Id),
            "assignee" => desc
                ? query.OrderByDescending(task => task.AssigneeName).ThenBy(task => task.Id)
                : query.OrderBy(task => task.AssigneeName).ThenBy(task => task.Id),
            "author" => desc
                ? query.OrderByDescending(task => task.AuthorName).ThenBy(task => task.Id)
                : query.OrderBy(task => task.AuthorName).ThenBy(task => task.Id),
            "type" => desc
                ? query.OrderByDescending(task => task.Type).ThenBy(task => task.Id)
                : query.OrderBy(task => task.Type).ThenBy(task => task.Id),
            "result" => desc
                ? query.OrderByDescending(task => task.Result ?? string.Empty).ThenBy(task => task.Id)
                : query.OrderBy(task => task.Result ?? string.Empty).ThenBy(task => task.Id),
            "status" => desc
                ? query.OrderByDescending(task => task.Status).ThenBy(task => task.Id)
                : query.OrderBy(task => task.Status).ThenBy(task => task.Id),
            "dueat" or "duedate" or "date" => desc
                ? query.OrderByDescending(task => task.DueAt).ThenBy(task => task.Id)
                : query.OrderBy(task => task.DueAt).ThenBy(task => task.Id),
            _ => desc
                ? query.OrderByDescending(task => task.DueAt).ThenBy(task => task.Id)
                : query.OrderBy(task => task.DueAt).ThenBy(task => task.Id),
        };
    }

    private static string? NormalizePlanningReportFormat(string? format)
    {
        var normalized = (format ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant();
        return normalized switch
        {
            "html" => "html",
            "word" or "doc" or "docx" => "word",
            "pdf" => "pdf",
            _ => null,
        };
    }

    private static string BuildTaskPlanningReportFilterText(string? status, string? type, string? priority, DateTime? from, DateTime? to)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(status) && !string.Equals(status, "all", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(string.Equals(status, "active", StringComparison.OrdinalIgnoreCase)
                ? "статус: активные"
                : $"статус: {TaskStatusLabel(NormalizeEnumValue(status, AllowedStatuses, "new")).ToLowerInvariant()}");
        }

        if (!string.IsNullOrWhiteSpace(type) && !string.Equals(type, "all", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add($"тип: {TaskTypeLabel(NormalizeEnumValue(type, AllowedTypes, "task")).ToLowerInvariant()}");
        }

        if (!string.IsNullOrWhiteSpace(priority) && !string.Equals(priority, "all", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(string.Equals(priority, "highPriority", StringComparison.OrdinalIgnoreCase)
                ? "приоритет: высокий или срочный"
                : $"приоритет: {TaskPriorityLabel(NormalizeEnumValue(priority, AllowedPriorities, "normal")).ToLowerInvariant()}");
        }

        if (from.HasValue || to.HasValue)
        {
            var fromText = from.HasValue ? FormatPlanningDate(from.Value) : "без начала";
            var toText = to.HasValue ? FormatPlanningDate(to.Value) : "без окончания";
            parts.Add($"срок: {fromText} - {toText}");
        }

        return parts.Count == 0 ? "Все доступные задачи" : string.Join("; ", parts);
    }

    private static string BuildTaskPlanningReportHtml(
        IReadOnlyList<TaskJournalItem> tasks,
        string filterText,
        DateTime generatedAtUtc,
        TimeZoneInfo timeZone)
    {
        var html = new StringBuilder();
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html lang=\"ru\">");
        html.AppendLine("<head>");
        html.AppendLine("<meta charset=\"utf-8\">");
        html.AppendLine("<title>Планинг по клиентам</title>");
        html.AppendLine("<style>");
        html.AppendLine("@page { size: A4 landscape; margin: 12mm; }");
        html.AppendLine("body { font-family: Arial, sans-serif; color: #111827; font-size: 11px; }");
        html.AppendLine("h1 { margin: 0 0 8px; text-align: center; font-size: 20px; letter-spacing: .04em; }");
        html.AppendLine(".meta { margin: 0 0 14px; color: #475569; text-align: center; }");
        html.AppendLine("table { width: 100%; border-collapse: collapse; table-layout: fixed; }");
        html.AppendLine("th, td { border: 1px solid #94a3b8; padding: 6px 7px; vertical-align: top; word-wrap: break-word; }");
        html.AppendLine("th { background: #e2e8f0; text-transform: uppercase; font-size: 9px; letter-spacing: .08em; }");
        html.AppendLine(".client-row td { background: #dbeafe; font-weight: 700; font-size: 12px; }");
        html.AppendLine(".case-row td { background: #f1f5f9; font-weight: 700; }");
        html.AppendLine(".empty-row td { text-align: center; color: #64748b; padding: 18px; }");
        html.AppendLine(".small { color: #64748b; font-size: 10px; margin-top: 3px; }");
        html.AppendLine(".overdue-note { color: #dc2626; font-size: 10px; font-weight: 700; margin-top: 3px; }");
        html.AppendLine(".num { width: 5%; text-align: center; }");
        html.AppendLine(".kind { width: 10%; }");
        html.AppendLine(".task { width: 28%; }");
        html.AppendLine(".due { width: 12%; }");
        html.AppendLine(".status { width: 12%; }");
        html.AppendLine(".assignee { width: 16%; }");
        html.AppendLine(".comment { width: 17%; }");
        html.AppendLine("</style>");
        html.AppendLine("</head>");
        html.AppendLine("<body>");
        html.AppendLine("<h1>ПЛАНИНГ ПО КЛИЕНТАМ</h1>");
        html.AppendLine($"<p class=\"meta\">Сформировано: {Html(FormatPlanningDate(generatedAtUtc, timeZone))}. Фильтр: {Html(filterText)}. Задач: {tasks.Count}.</p>");
        html.AppendLine("<table>");

        if (tasks.Count == 0)
        {
            html.AppendLine("<tr class=\"empty-row\"><td colspan=\"7\">По выбранным фильтрам задач нет.</td></tr>");
        }
        else
        {
            foreach (var clientGroup in tasks
                .GroupBy(GetPlanningClientName)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
            {
                html.AppendLine($"<tr class=\"client-row\"><td colspan=\"7\">КЛИЕНТ | {Html(clientGroup.Key)}</td></tr>");

                foreach (var caseGroup in clientGroup
                    .GroupBy(GetPlanningCaseTitle)
                    .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
                {
                    html.AppendLine($"<tr class=\"case-row\"><td colspan=\"7\">ДЕЛО | {Html(caseGroup.Key)}</td></tr>");
                    AppendTaskPlanningHeader(html);

                    var rowNumber = 1;
                    foreach (var task in caseGroup.OrderBy(item => item.DueAt).ThenBy(item => item.Id))
                    {
                        var description = Html(task.Title);
                        if (!string.IsNullOrWhiteSpace(task.Description) &&
                            !string.Equals(task.Description.Trim(), task.Title.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            description += $"<div class=\"small\">{Html(task.Description)}</div>";
                        }

                        html.AppendLine("<tr>");
                        html.AppendLine($"<td class=\"num\">{rowNumber}</td>");
                        html.AppendLine($"<td class=\"kind\">{Html(TaskTypeLabel(task.Type))}</td>");
                        html.AppendLine($"<td class=\"task\">{description}</td>");
                        html.AppendLine($"<td class=\"due\">{Html(FormatPlanningDate(task.DueAt, timeZone))}</td>");
                        var statusHtml = Html(TaskStatusLabel(ResolveTaskStatus(task)));
                        if (IsTaskDueOverdue(task, generatedAtUtc))
                        {
                            statusHtml += "<div class=\"overdue-note\">Просрочена</div>";
                        }

                        html.AppendLine($"<td class=\"status\">{statusHtml}</td>");
                        html.AppendLine($"<td class=\"assignee\">{Html(GetPlanningAssigneeName(task))}</td>");
                        html.AppendLine($"<td class=\"comment\">{Html(task.Result)}</td>");
                        html.AppendLine("</tr>");
                        rowNumber++;
                    }
                }
            }
        }

        html.AppendLine("</table>");
        html.AppendLine("</body>");
        html.AppendLine("</html>");
        return html.ToString();
    }

    private static byte[] BuildTaskPlanningWordDocument(
        IReadOnlyList<TaskJournalItem> tasks,
        string filterText,
        DateTime generatedAtUtc,
        TimeZoneInfo timeZone)
    {
        var items = tasks.Select(task => new TaskPlanningWordItem(
            GetPlanningClientName(task),
            GetPlanningCaseTitle(task),
            TaskTypeLabel(task.Type),
            task.Title,
            task.Description,
            FormatPlanningDate(task.DueAt, timeZone),
            TaskStatusLabel(ResolveTaskStatus(task)),
            IsTaskDueOverdue(task, generatedAtUtc),
            GetPlanningAssigneeName(task),
            task.Result)).ToArray();

        return TaskPlanningWordDocumentBuilder.Build(
            items,
            FormatPlanningDate(generatedAtUtc, timeZone),
            filterText);
    }

    private static void AppendTaskPlanningHeader(StringBuilder html)
    {
        html.AppendLine("<tr>");
        html.AppendLine("<th class=\"num\">№ П/П</th>");
        html.AppendLine("<th class=\"kind\">ЗАДАЧА</th>");
        html.AppendLine("<th class=\"task\">ОПИСАНИЕ ЗАДАЧИ</th>");
        html.AppendLine("<th class=\"due\">СРОК</th>");
        html.AppendLine("<th class=\"status\">СТАТУС</th>");
        html.AppendLine("<th class=\"assignee\">ИСПОЛНИТЕЛЬ</th>");
        html.AppendLine("<th class=\"comment\">КОММЕНТАРИЙ</th>");
        html.AppendLine("</tr>");
    }

    private static string GetPlanningClientName(TaskJournalItem task)
    {
        if (task.Client is not null && !string.IsNullOrWhiteSpace(task.Client.Name))
        {
            return task.Client.Name.Trim();
        }

        if (task.LegalEntity is not null)
        {
            var legalEntityName = GetLegalEntityName(task.LegalEntity);
            if (!string.IsNullOrWhiteSpace(legalEntityName))
            {
                return legalEntityName;
            }
        }

        return !string.IsNullOrWhiteSpace(task.ObjectName)
            ? task.ObjectName.Trim()
            : "Без клиента";
    }

    private static string GetPlanningCaseTitle(TaskJournalItem task)
    {
        if (task.ClientCase is not null && !string.IsNullOrWhiteSpace(task.ClientCase.Title))
        {
            return task.ClientCase.Title.Trim();
        }

        return !string.IsNullOrWhiteSpace(task.ObjectName)
            ? task.ObjectName.Trim()
            : "Без дела";
    }

    private static string GetPlanningAssigneeName(TaskJournalItem task)
    {
        var names = new List<string>();
        if (!string.IsNullOrWhiteSpace(task.AssigneeName))
        {
            names.Add(task.AssigneeName.Trim());
        }

        names.AddRange(task.Participants
            .Where(participant => string.Equals(participant.Role, "assignee", StringComparison.OrdinalIgnoreCase))
            .Select(participant => participant.DisplayName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim()));

        var distinct = names
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return distinct.Count == 0 ? "—" : string.Join(", ", distinct);
    }

    private static string FormatPlanningDate(DateTime value, TimeZoneInfo? timeZone = null)
    {
        var localValue = timeZone is null
            ? value
            : CalendarTimeZoneHelper.ToUserLocal(NormalizeUtcInstant(value), timeZone);
        return localValue.TimeOfDay == TimeSpan.Zero
            ? localValue.ToString("dd.MM.yyyy", CultureInfo.GetCultureInfo("ru-RU"))
            : localValue.ToString("dd.MM.yyyy HH:mm", CultureInfo.GetCultureInfo("ru-RU"));
    }

    private static string Html(string? value)
        => WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(value) ? "—" : value.Trim());

    private IQueryable<TaskJournalItem> BuildVisibleTasksQuery(TaskAccess access)
    {
        return BuildVisibleTaskBaseQuery(access)
            .Include(task => task.Client)
            .Include(task => task.LegalEntity)
            .Include(task => task.ClientCase)
            .Include(task => task.BillingContract)
            .Include(task => task.BillingInvoice)
            .Include(task => task.ContractorPayment)
            .Include(task => task.Participants)
            .AsQueryable();
    }

    private static IQueryable<TaskJournalItem> ProjectTaskReadItems(IQueryable<TaskJournalItem> query)
        => query.Select(task => new TaskJournalItem
        {
            Id = task.Id,
            ClientId = task.ClientId,
            Client = task.Client == null
                ? null
                : new Client
                {
                    Id = task.Client.Id,
                    Name = task.Client.Name,
                },
            LegalEntityId = task.LegalEntityId,
            LegalEntity = task.LegalEntity == null
                ? null
                : new LegalEntity
                {
                    Id = task.LegalEntity.Id,
                    ShortName = task.LegalEntity.ShortName,
                    FullName = task.LegalEntity.FullName,
                },
            ClientCaseId = task.ClientCaseId,
            ClientCase = task.ClientCase == null
                ? null
                : new ClientCase
                {
                    Id = task.ClientCase.Id,
                    Title = task.ClientCase.Title,
                },
            ClientBillingClassification = task.ClientBillingClassification,
            BillingContractId = task.BillingContractId,
            BillingContract = task.BillingContract == null
                ? null
                : new Contract
                {
                    Id = task.BillingContract.Id,
                    Number = task.BillingContract.Number,
                },
            BillingModeSnapshot = task.BillingModeSnapshot,
            BillingInvoiceId = task.BillingInvoiceId,
            BillingInvoice = task.BillingInvoice == null
                ? null
                : new Payment
                {
                    Id = task.BillingInvoice.Id,
                    Account = task.BillingInvoice.Account,
                },
            ObjectName = task.ObjectName,
            Title = task.Title,
            Description = task.Description,
            AssigneeUserId = task.AssigneeUserId,
            AssigneeName = task.AssigneeName,
            AuthorUserId = task.AuthorUserId,
            AuthorName = task.AuthorName,
            DueAt = task.DueAt,
            ApprovalDueAt = task.ApprovalDueAt,
            PlannedDurationMinutes = task.PlannedDurationMinutes,
            ContractorPaymentEnabled = task.ContractorPaymentEnabled,
            ContractorPaymentAmount = task.ContractorPaymentAmount,
            ContractorPaymentDueDate = task.ContractorPaymentDueDate,
            ContractorPaymentOnResult = task.ContractorPaymentOnResult,
            ContractorPaymentResultDelayDays = task.ContractorPaymentResultDelayDays,
            ContractorPaymentId = task.ContractorPaymentId,
            ContractorPayment = task.ContractorPayment == null
                ? null
                : new Payment
                {
                    Id = task.ContractorPayment.Id,
                    Status = task.ContractorPayment.Status,
                },
            Type = task.Type,
            Priority = task.Priority,
            Status = task.Status,
            IsUnderControl = task.IsUnderControl,
            ControlMarkedAt = task.ControlMarkedAt,
            ControlMarkedByUserId = task.ControlMarkedByUserId,
            ControlMarkedByUserName = task.ControlMarkedByUserName,
            Result = task.Result,
            RemindersJson = task.RemindersJson,
            ChannelsJson = task.ChannelsJson,
            TagsJson = task.TagsJson,
            CreatedAt = task.CreatedAt,
            UpdatedAt = task.UpdatedAt,
            Participants = task.Participants
                .Select(participant => new TaskJournalParticipant
                {
                    Id = participant.Id,
                    TaskJournalItemId = participant.TaskJournalItemId,
                    UserId = participant.UserId,
                    Role = participant.Role,
                    DisplayName = participant.DisplayName,
                    Email = participant.Email,
                    SortOrder = participant.SortOrder,
                    ApprovalStatus = participant.ApprovalStatus,
                    Resolution = participant.Resolution,
                    RespondedAt = participant.RespondedAt,
                })
                .ToList(),
        });

    private IQueryable<TaskJournalItem> BuildVisibleTaskBaseQuery(TaskAccess access)
        => _db.TaskJournalItems.ApplyTaskVisibility(access.Visibility);

    private async Task<TaskAccess?> GetTaskAccessAsync(CancellationToken ct, string section = RolePermissionCatalog.Tasks)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return null;
        }

        var visibility = await _db.ResolveDataVisibilityAsync(User, section, ct);
        if (visibility is null)
        {
            return null;
        }

        var isAdmin = visibility.IsAdmin;
        var permission = await _db.GetUserSectionPermissionAsync(userId.Value, section, ct);
        return new TaskAccess(userId.Value, User.GetDisplayName(), isAdmin, permission, visibility);
    }

    private async Task<TaskJournalSettingsDto> GetTaskJournalSettingsAsync(CancellationToken ct)
    {
        var settings = await _db.SystemSettings
            .AsNoTracking()
            .Where(item =>
                item.Key == RestrictExecutorEditingSettingKey ||
                item.Key == AllowExecutorDeadlineChangeRequestsSettingKey ||
                item.Key == ExecutorCompletionAllowedUserIdsSettingKey ||
                item.Key == RequireCompletionResultSettingKey ||
                item.Key == DailyOverdueReminderEnabledSettingKey ||
                item.Key == BirthdayTaskAutomationEnabledSettingKey ||
                item.Key == EmployeeBirthdayTaskAutomationEnabledSettingKey ||
                item.Key == BirthdayTaskLeadDaysSettingKey ||
                item.Key == BirthdayClientUseSelectedAssigneeSettingKey ||
                item.Key == BirthdayTaskAssigneeUserIdSettingKey ||
                item.Key == AllowAdminCompleteAnyTaskSettingKey ||
                item.Key == FinancialDocumentsResponsibleUserSettingKey ||
                item.Key == ApprovalDueDaysSettingKey ||
                item.Key == TaskCardWindowModeSettingKey ||
                item.Key == ApprovalQuickActionModeSettingKey ||
                item.Key == TaskStickerStylesSettingKey ||
                item.Key == CaseInactivityAutomationEnabledSettingKey ||
                item.Key == CaseInactivityAutomationDaysSettingKey ||
                item.Key == CaseInactivityAutomationLimitSettingKey ||
                item.Key == CaseInactivityAutomationDefaultAssigneeSettingKey ||
                item.Key == CaseInactivityAutomationEscalationDaysSettingKey ||
                item.Key == CaseInactivityAutomationEscalationUserSettingKey ||
                item.Key == CaseInactivityAutomationRunLocalTimeSettingKey ||
                item.Key == CaseInactivityAutomationScheduleTimeZoneSettingKey)
            .ToDictionaryAsync(item => item.Key, item => item.Value, ct);

        var defaultAssigneeUserId = ParseIntSetting(settings.GetValueOrDefault(CaseInactivityAutomationDefaultAssigneeSettingKey), 0);
        var escalationUserId = ParseIntSetting(settings.GetValueOrDefault(CaseInactivityAutomationEscalationUserSettingKey), 0);
        var financialDocumentsResponsibleUserId = ParseIntSetting(settings.GetValueOrDefault(FinancialDocumentsResponsibleUserSettingKey), 0);
        var birthdayAssigneeUserId = ParseIntSetting(settings.GetValueOrDefault(BirthdayTaskAssigneeUserIdSettingKey), 0);
        var birthdayAssigneeName = birthdayAssigneeUserId > 0
            ? await _db.Users.AsNoTracking()
                .Where(user => user.Id == birthdayAssigneeUserId && user.IsActive && user.IsApproved)
                .Select(user => user.FullName ?? user.Email)
                .FirstOrDefaultAsync(ct) ?? string.Empty
            : string.Empty;
        var financialDocumentsResponsibleUserName = financialDocumentsResponsibleUserId > 0
            ? await _db.Users.AsNoTracking()
                .Where(user => user.Id == financialDocumentsResponsibleUserId && user.IsActive && user.IsApproved)
                .Select(user => user.FullName ?? user.Email)
                .FirstOrDefaultAsync(ct) ?? string.Empty
            : string.Empty;
        var effectiveFinancialDocumentsResponsibleUserId = string.IsNullOrWhiteSpace(financialDocumentsResponsibleUserName)
            ? 0
            : financialDocumentsResponsibleUserId;
        var inactivityDays = Math.Clamp(ParseIntSetting(settings.GetValueOrDefault(CaseInactivityAutomationDaysSettingKey), 14), 1, 365);
        var stickerStyles = ParseTaskStickerStyles(settings.GetValueOrDefault(TaskStickerStylesSettingKey));
        return new TaskJournalSettingsDto
        {
            RestrictExecutorEditing = ParseBoolSetting(settings.GetValueOrDefault(RestrictExecutorEditingSettingKey), true),
            AllowExecutorDeadlineChangeRequests = ParseBoolSetting(settings.GetValueOrDefault(AllowExecutorDeadlineChangeRequestsSettingKey), true),
            ExecutorCompletionAllowedUserIds = ParseUserIdListSetting(settings.GetValueOrDefault(ExecutorCompletionAllowedUserIdsSettingKey)),
            RequireCompletionResult = ParseBoolSetting(settings.GetValueOrDefault(RequireCompletionResultSettingKey), true),
            DailyOverdueReminderEnabled = ParseBoolSetting(settings.GetValueOrDefault(DailyOverdueReminderEnabledSettingKey), true),
            BirthdayTaskAutomationEnabled = ParseBoolSetting(settings.GetValueOrDefault(BirthdayTaskAutomationEnabledSettingKey), false),
            EmployeeBirthdayTaskAutomationEnabled = ParseBoolSetting(settings.GetValueOrDefault(EmployeeBirthdayTaskAutomationEnabledSettingKey), false),
            BirthdayTaskLeadDays = Math.Clamp(ParseIntSetting(settings.GetValueOrDefault(BirthdayTaskLeadDaysSettingKey), 7), 0, 60),
            BirthdayClientUseSelectedAssignee = ParseBoolSetting(settings.GetValueOrDefault(BirthdayClientUseSelectedAssigneeSettingKey), false),
            BirthdayTaskAssigneeUserId = string.IsNullOrWhiteSpace(birthdayAssigneeName) ? null : birthdayAssigneeUserId,
            BirthdayTaskAssigneeUserName = birthdayAssigneeName,
            AllowAdminCompleteAnyTask = ParseBoolSetting(settings.GetValueOrDefault(AllowAdminCompleteAnyTaskSettingKey), true),
            FinancialDocumentsResponsibleUserId = effectiveFinancialDocumentsResponsibleUserId > 0 ? effectiveFinancialDocumentsResponsibleUserId : null,
            FinancialDocumentsResponsibleUserName = financialDocumentsResponsibleUserName,
            ApprovalDueDays = Math.Clamp(ParseIntSetting(settings.GetValueOrDefault(ApprovalDueDaysSettingKey), DefaultApprovalDueDays), 1, 365),
            TaskCardWindowMode = NormalizeTaskCardWindowMode(settings.GetValueOrDefault(TaskCardWindowModeSettingKey)),
            ApprovalQuickActionMode = NormalizeApprovalQuickActionMode(settings.GetValueOrDefault(ApprovalQuickActionModeSettingKey)),
            CaseInactivityAutomationEnabled = ParseBoolSetting(settings.GetValueOrDefault(CaseInactivityAutomationEnabledSettingKey), true),
            CaseInactivityDays = inactivityDays,
            CaseInactivityLimit = Math.Clamp(ParseIntSetting(settings.GetValueOrDefault(CaseInactivityAutomationLimitSettingKey), 25), 1, 100),
            CaseInactivityDefaultAssigneeUserId = defaultAssigneeUserId > 0 ? defaultAssigneeUserId : null,
            CaseInactivityEscalationDays = Math.Clamp(ParseIntSetting(settings.GetValueOrDefault(CaseInactivityAutomationEscalationDaysSettingKey), 30), inactivityDays, 365),
            CaseInactivityEscalationUserId = escalationUserId > 0 ? escalationUserId : null,
            CaseInactivityFirstRepeatAfterDays = CaseInactivityControlStateMachine.FirstRepeatOffsetDays,
            CaseInactivityCriticalRepeatAfterDays = CaseInactivityControlStateMachine.SecondRepeatOffsetDays,
            CaseInactivityRunLocalTime = TimeOnly.TryParseExact(
                settings.GetValueOrDefault(CaseInactivityAutomationRunLocalTimeSettingKey),
                "HH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var runLocalTime)
                ? runLocalTime.ToString("HH:mm", CultureInfo.InvariantCulture)
                : "09:00",
            CaseInactivityScheduleTimeZone = CalendarTimeZoneHelper.ResolveIanaTimeZoneId(
                CalendarTimeZoneHelper.ResolveTimeZone(
                    settings.GetValueOrDefault(CaseInactivityAutomationScheduleTimeZoneSettingKey))),
            StickerStyles = stickerStyles,
        };
    }

    private static Dictionary<string, TaskStickerStyle> ParseTaskStickerStyles(string? rawValue)
    {
        if (!string.IsNullOrWhiteSpace(rawValue))
        {
            try
            {
                var storedStyles = JsonSerializer.Deserialize<Dictionary<string, TaskStickerStyle>>(rawValue, JsonOptions);
                if (storedStyles is not null &&
                    TryNormalizeTaskStickerStyles(storedStyles, rejectUnknownKeys: false, out var normalized, out _))
                {
                    return normalized;
                }
            }
            catch (JsonException)
            {
                // Поврежденная настройка не должна ломать журнал задач: возвращаем безопасные значения.
            }
        }

        return CloneDefaultTaskStickerStyles();
    }

    private static bool TryNormalizeTaskStickerStyles(
        IReadOnlyDictionary<string, TaskStickerStyle> requestedStyles,
        bool rejectUnknownKeys,
        out Dictionary<string, TaskStickerStyle> normalizedStyles,
        out string error)
    {
        if (rejectUnknownKeys)
        {
            var unknownKey = requestedStyles.Keys.FirstOrDefault(key => !DefaultTaskStickerStyles.ContainsKey(key));
            if (unknownKey is not null)
            {
                normalizedStyles = [];
                error = $"Неизвестный тип стикера: {unknownKey}.";
                return false;
            }
        }

        normalizedStyles = CloneDefaultTaskStickerStyles();
        foreach (var (key, defaultStyle) in DefaultTaskStickerStyles)
        {
            if (!requestedStyles.TryGetValue(key, out var requestedStyle) || requestedStyle is null)
            {
                continue;
            }

            var label = requestedStyle.Label?.Trim();
            var shortLabel = requestedStyle.ShortLabel?.Trim();
            if (string.IsNullOrWhiteSpace(label) || label.Length > 80)
            {
                error = $"Полное название стикера «{defaultStyle.Label}» должно содержать от 1 до 80 символов.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(shortLabel) || shortLabel.Length > 30)
            {
                error = $"Короткое название стикера «{defaultStyle.Label}» должно содержать от 1 до 30 символов.";
                return false;
            }

            if (!TryNormalizeHexColor(requestedStyle.BackgroundColor, out var backgroundColor) ||
                !TryNormalizeHexColor(requestedStyle.TextColor, out var textColor) ||
                !TryNormalizeHexColor(requestedStyle.BorderColor, out var borderColor))
            {
                error = $"Цвета стикера «{defaultStyle.Label}» должны быть указаны в формате #RRGGBB.";
                return false;
            }

            normalizedStyles[key] = new TaskStickerStyle
            {
                Label = label,
                ShortLabel = shortLabel,
                BackgroundColor = backgroundColor,
                TextColor = textColor,
                BorderColor = borderColor,
            };
        }

        error = string.Empty;
        return true;
    }

    private static Dictionary<string, TaskStickerStyle> CloneDefaultTaskStickerStyles()
        => DefaultTaskStickerStyles.ToDictionary(
            pair => pair.Key,
            pair => new TaskStickerStyle
            {
                Label = pair.Value.Label,
                ShortLabel = pair.Value.ShortLabel,
                BackgroundColor = pair.Value.BackgroundColor,
                TextColor = pair.Value.TextColor,
                BorderColor = pair.Value.BorderColor,
            },
            StringComparer.Ordinal);

    private static bool TryNormalizeHexColor(string? value, out string normalized)
    {
        normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized.Length == 7 &&
               normalized[0] == '#' &&
               normalized.AsSpan(1).IndexOfAnyExcept("0123456789abcdef") < 0;
    }

    private static string NormalizeTaskCardWindowMode(string? value)
    {
        return string.Equals(value, TaskCardWindowModeModern, StringComparison.OrdinalIgnoreCase)
            ? TaskCardWindowModeModern
            : TaskCardWindowModeLegacy;
    }

    private static string NormalizeApprovalQuickActionMode(string? value)
    {
        return string.Equals(value, ApprovalQuickActionModeSeparateButton, StringComparison.OrdinalIgnoreCase)
            ? ApprovalQuickActionModeSeparateButton
            : ApprovalQuickActionModeCompletionButton;
    }

    private static string NormalizeApprovalFinalAction(string? value)
    {
        if (string.Equals(value, "complete", StringComparison.OrdinalIgnoreCase))
        {
            return "complete";
        }

        if (string.Equals(value, "returnToAssignee", StringComparison.OrdinalIgnoreCase))
        {
            return "returnToAssignee";
        }

        return string.Empty;
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
        => int.TryParse(value, out var parsed) ? parsed : fallback;

    private static List<int> ParseUserIdListSetting(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        try
        {
            return (JsonSerializer.Deserialize<int[]>(value, JsonOptions) ?? [])
                .Where(id => id > 0)
                .Distinct()
                .OrderBy(id => id)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task<TaskParticipantsResolution> ResolveParticipantsAsync(
        UpsertTaskJournalItemRequest request,
        int fallbackUserId,
        CancellationToken ct)
    {
        if (request.IsUntriaged)
        {
            if (!request.IsInternal || request.ClientId.HasValue || request.LegalEntityId.HasValue
                || request.ClientCaseId.HasValue || request.AssigneeUserId.HasValue
                || !string.IsNullOrWhiteSpace(request.AssigneeName) || request.Participants.Count > 0)
            {
                return TaskParticipantsResolution.Invalid("Неразобранная задача должна быть внутренней, без клиента и исполнителя.");
            }

            return TaskParticipantsResolution.Valid(null, string.Empty, []);
        }

        var drafts = new List<TaskParticipantDraft>();
        var index = 0;

        foreach (var participant in request.Participants)
        {
            var role = NormalizeEnumValue(participant.Role, AllowedParticipantRoles, string.Empty);
            if (string.IsNullOrWhiteSpace(role))
            {
                return TaskParticipantsResolution.Invalid("Участник задачи указан с некорректной ролью.");
            }

            User? user = null;
            if (participant.UserId.HasValue)
            {
                user = await _db.Users
                    .AsNoTracking()
                    .FirstOrDefaultAsync(item => item.Id == participant.UserId.Value && item.IsActive && item.IsApproved, ct);
                if (user is null)
                {
                    return TaskParticipantsResolution.Invalid("Указанный участник задачи не найден.");
                }
            }

            if (string.Equals(role, "approver", StringComparison.OrdinalIgnoreCase) && user is null)
            {
                return TaskParticipantsResolution.Invalid("Согласующий должен быть выбран из пользователей системы.");
            }

            var displayName = user is null
                ? NormalizeOptional(participant.Name)
                : BuildUserDisplayName(user.FullName, user.Email);
            if (string.IsNullOrWhiteSpace(displayName))
            {
                return TaskParticipantsResolution.Invalid("Укажите имя участника задачи.");
            }

            var approvalStatus = string.Equals(role, "approver", StringComparison.OrdinalIgnoreCase)
                ? NormalizeEnumValue(participant.ApprovalStatus, AllowedApprovalStatuses, "pending")
                : "pending";

            drafts.Add(new TaskParticipantDraft(
                user?.Id,
                displayName,
                user?.Email ?? string.Empty,
                role,
                participant.SortOrder ?? index,
                approvalStatus,
                NormalizeOptional(participant.Resolution)));
            index++;
        }

        var primaryAssignee = await ResolveAssigneeAsync(request, fallbackUserId, ct);
        if (primaryAssignee is null)
        {
            return TaskParticipantsResolution.Invalid("Указанный исполнитель не найден.");
        }

        var primaryEmail = string.Empty;
        if (primaryAssignee.Value.UserId.HasValue)
        {
            primaryEmail = await _db.Users
                .AsNoTracking()
                .Where(item => item.Id == primaryAssignee.Value.UserId.Value)
                .Select(item => item.Email)
                .FirstOrDefaultAsync(ct) ?? string.Empty;
        }

        var hasPrimaryAssignee = drafts.Any(item =>
            string.Equals(item.Role, "assignee", StringComparison.OrdinalIgnoreCase)
            && ParticipantMatches(item, primaryAssignee.Value.UserId, primaryAssignee.Value.Name));
        if (!hasPrimaryAssignee)
        {
            drafts.Insert(0, new TaskParticipantDraft(
                primaryAssignee.Value.UserId,
                primaryAssignee.Value.Name,
                primaryEmail,
                "assignee",
                0,
                "pending",
                null));
        }

        var normalized = drafts
            .GroupBy(item => ParticipantKey(item), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select((item, position) => item with { SortOrder = item.SortOrder == 0 ? position : item.SortOrder })
            .ToList();

        return TaskParticipantsResolution.Valid(
            primaryAssignee.Value.UserId,
            primaryAssignee.Value.Name,
            normalized);
    }

    private async Task<(int? UserId, string Name, string? Error)> ResolveTemplateAssigneeAsync(
        UpsertTaskJournalTemplateRequest request,
        CancellationToken ct)
    {
        if (request.AssigneeUserId.HasValue)
        {
            var user = await _db.Users
                .AsNoTracking()
                .Where(item => item.Id == request.AssigneeUserId.Value && item.IsActive && item.IsApproved)
                .Select(item => new { item.Id, item.FullName, item.Email })
                .FirstOrDefaultAsync(ct);

            if (user is null)
            {
                return (null, string.Empty, "Указанный исполнитель шаблона не найден.");
            }

            return (user.Id, string.IsNullOrWhiteSpace(user.FullName) ? user.Email : user.FullName, null);
        }

        return (null, NormalizeOptional(request.AssigneeName) ?? string.Empty, null);
    }

    private async Task<TaskTemplateParticipantsResolution> ResolveTemplateParticipantsAsync(
        UpsertTaskJournalTemplateRequest request,
        CancellationToken ct)
    {
        var drafts = new List<TaskTemplateParticipantDraft>();
        var index = 0;

        foreach (var participant in request.Participants)
        {
            var role = NormalizeEnumValue(participant.Role, AllowedParticipantRoles, string.Empty);
            if (string.IsNullOrWhiteSpace(role))
            {
                return TaskTemplateParticipantsResolution.Invalid("Участник шаблона указан с некорректной ролью.");
            }

            User? user = null;
            if (participant.UserId.HasValue)
            {
                user = await _db.Users
                    .AsNoTracking()
                    .FirstOrDefaultAsync(item => item.Id == participant.UserId.Value && item.IsActive && item.IsApproved, ct);
                if (user is null)
                {
                    return TaskTemplateParticipantsResolution.Invalid("Указанный участник шаблона не найден.");
                }
            }

            if (string.Equals(role, "approver", StringComparison.OrdinalIgnoreCase) && user is null)
            {
                return TaskTemplateParticipantsResolution.Invalid("Согласующий в шаблоне должен быть выбран из пользователей системы.");
            }

            var displayName = user is null
                ? NormalizeOptional(participant.Name)
                : BuildUserDisplayName(user.FullName, user.Email);
            if (string.IsNullOrWhiteSpace(displayName))
            {
                return TaskTemplateParticipantsResolution.Invalid("Укажите имя участника шаблона.");
            }

            drafts.Add(new TaskTemplateParticipantDraft(
                user?.Id,
                displayName,
                user?.Email ?? string.Empty,
                role,
                participant.SortOrder ?? index));
            index++;
        }

        var normalized = drafts
            .GroupBy(item => $"{item.Role}:{item.UserId?.ToString(CultureInfo.InvariantCulture) ?? item.DisplayName}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select((item, position) => item with { SortOrder = item.SortOrder == 0 ? position : item.SortOrder })
            .ToList();

        return TaskTemplateParticipantsResolution.Valid(normalized);
    }

    private async Task<(int? UserId, string Name)?> ResolveAssigneeAsync(
        UpsertTaskJournalItemRequest request,
        int fallbackUserId,
        CancellationToken ct)
    {
        if (request.AssigneeUserId.HasValue)
        {
            var user = await _db.Users
                .AsNoTracking()
                .Where(item => item.Id == request.AssigneeUserId.Value && item.IsActive && item.IsApproved)
                .Select(item => new { item.Id, item.FullName, item.Email })
                .FirstOrDefaultAsync(ct);

            return user is null
                ? null
                : (user.Id, string.IsNullOrWhiteSpace(user.FullName) ? user.Email : user.FullName);
        }

        var name = NormalizeOptional(request.AssigneeName);
        if (!string.IsNullOrWhiteSpace(name))
        {
            return (null, name);
        }

        var fallback = await _db.Users
            .AsNoTracking()
            .Where(item => item.Id == fallbackUserId && item.IsActive)
            .Select(item => new { item.Id, item.FullName, item.Email })
            .FirstOrDefaultAsync(ct);

        return fallback is null
            ? null
            : (fallback.Id, string.IsNullOrWhiteSpace(fallback.FullName) ? fallback.Email : fallback.FullName);
    }

    private async Task<TaskClientBinding> ResolveClientBindingAsync(
        UpsertTaskJournalItemRequest request,
        CancellationToken ct)
    {
        var hasClient = request.ClientId is > 0;
        var hasLegalEntity = request.LegalEntityId is > 0;
        if (request.IsInternal)
        {
            return TaskClientBinding.Valid(null, InternalTaskObjectName, null, string.Empty, null, null);
        }

        if (hasClient == hasLegalEntity)
        {
            return TaskClientBinding.Invalid("Укажите одного контрагента задачи: клиента или юрлицо.");
        }

        if (hasClient)
        {
            var client = await _db.Clients
                .AsNoTracking()
                .Where(item => item.Id == request.ClientId!.Value)
                .Select(item => new { item.Id, item.Name })
                .FirstOrDefaultAsync(ct);
            if (client is null)
            {
                return TaskClientBinding.Invalid("Указанный клиент не найден.");
            }

            if (!request.ClientCaseId.HasValue || request.ClientCaseId.Value <= 0)
            {
                return TaskClientBinding.Valid(client.Id, client.Name, null, string.Empty, null, null);
            }

            var clientCase = await _db.ClientCases
                .AsNoTracking()
                .Where(item => item.Id == request.ClientCaseId.Value)
                .Select(item => new { item.Id, item.ClientId, item.LegalEntityId, item.Title })
                .FirstOrDefaultAsync(ct);
            if (clientCase is null)
            {
                return TaskClientBinding.Invalid("Указанное дело не найдено.");
            }

            if (clientCase.ClientId != client.Id)
            {
                return TaskClientBinding.Invalid("Выбранное дело не относится к указанному клиенту.");
            }

            return TaskClientBinding.Valid(client.Id, client.Name, null, string.Empty, clientCase.Id, clientCase.Title);
        }

        var legalEntity = await _db.LegalEntities
            .AsNoTracking()
            .Where(item => item.Id == request.LegalEntityId!.Value)
            .Select(item => new { item.Id, item.ShortName, item.FullName })
            .FirstOrDefaultAsync(ct);
        if (legalEntity is null)
        {
            return TaskClientBinding.Invalid("Указанное юрлицо не найдено.");
        }

        var legalEntityName = GetLegalEntityName(legalEntity.Id, legalEntity.ShortName, legalEntity.FullName);
        if (!request.ClientCaseId.HasValue || request.ClientCaseId.Value <= 0)
        {
            return TaskClientBinding.Valid(null, string.Empty, legalEntity.Id, legalEntityName, null, null);
        }

        var legalEntityCase = await _db.ClientCases
            .AsNoTracking()
            .Where(item => item.Id == request.ClientCaseId.Value)
            .Select(item => new { item.Id, item.ClientId, item.LegalEntityId, item.Title })
            .FirstOrDefaultAsync(ct);
        if (legalEntityCase is null)
        {
            return TaskClientBinding.Invalid("Указанное дело не найдено.");
        }

        if (legalEntityCase.LegalEntityId != legalEntity.Id)
        {
            return TaskClientBinding.Invalid("Выбранное дело не относится к указанному юрлицу.");
        }

        return TaskClientBinding.Valid(null, string.Empty, legalEntity.Id, legalEntityName, legalEntityCase.Id, legalEntityCase.Title);
    }

    private static string BuildObjectName(string clientName, string? caseTitle)
        => string.IsNullOrWhiteSpace(clientName)
            ? InternalTaskObjectName
            : string.IsNullOrWhiteSpace(caseTitle)
                ? clientName.Trim()
                : $"{clientName.Trim()} - {caseTitle.Trim()}";

    private async Task<TaskBillingDecision> ResolveTaskBillingDecisionAsync(
        UpsertTaskJournalItemRequest request,
        TaskClientBinding binding,
        CancellationToken ct)
    {
        if (!TaskClientBillingClassification.IsDecision(request.ClientBillingClassification))
        {
            return TaskBillingDecision.Legacy();
        }

        var classification = TaskClientBillingClassification.NormalizeDecision(request.ClientBillingClassification);
        if (classification == TaskClientBillingClassification.Internal || request.IsInternal)
        {
            return TaskBillingDecision.Valid(TaskClientBillingClassification.Internal, null, null);
        }

        if (!binding.ClientCaseId.HasValue)
        {
            return TaskBillingDecision.Invalid("Для клиентской платной задачи обязательно выберите дело.");
        }

        if (!request.BillingContractId.HasValue)
        {
            return TaskBillingDecision.Valid(TaskClientBillingClassification.Billable, null, null);
        }

        var contract = await _db.Contracts
            .AsNoTracking()
            .Where(item => item.Id == request.BillingContractId.Value)
            .Select(item => new
            {
                item.Id,
                item.BillingMode,
                item.Status,
                item.Date,
                item.ValidUntil,
                HasClient = binding.ClientId.HasValue && item.ClientContracts.Any(link => link.ClientId == binding.ClientId.Value),
                HasLegalEntity = binding.LegalEntityId.HasValue && item.LegalEntityContracts.Any(link => link.LegalEntityId == binding.LegalEntityId.Value),
            })
            .FirstOrDefaultAsync(ct);
        if (contract is null || (!contract.HasClient && !contract.HasLegalEntity))
        {
            return TaskBillingDecision.Invalid("Выбранный договор не относится к клиенту задачи.");
        }

        if (contract.Status is ContractStatus.Cancelled or ContractStatus.Terminated)
        {
            return TaskBillingDecision.Invalid("Нельзя использовать расторгнутый или отменённый договор.");
        }

        var today = DateTime.UtcNow.Date;
        if (contract.Date.Date > today || (contract.ValidUntil.HasValue && contract.ValidUntil.Value.Date < today))
        {
            return TaskBillingDecision.Invalid("Нельзя использовать договор, который ещё не начал действовать или уже истёк.");
        }

        return TaskBillingDecision.Valid(
            TaskClientBillingClassification.Billable,
            contract.Id,
            ContractBillingMode.Normalize(contract.BillingMode));
    }

    private static string ResolveTaskBillingStatus(TaskJournalItem entity)
    {
        if (entity.ClientBillingClassification == TaskClientBillingClassification.Legacy)
        {
            return "legacy";
        }

        if (entity.ClientBillingClassification == TaskClientBillingClassification.Internal)
        {
            return "internal";
        }

        if (entity.BillingInvoiceId.HasValue)
        {
            return "invoiced";
        }

        return entity.BillingModeSnapshot == ContractBillingMode.Fixed ? "fixed" : "unbilled";
    }

    private static DateTime ResolveLastCaseActivity(
        ClientCase clientCase,
        DateTime? lastTaskAt,
        DateTime? lastTaskCommentAt,
        DateTime? lastDocumentAt,
        DateTime? lastCourtEventAt)
        => ResolveLastCaseActivity(
            clientCase.CreatedAt,
            clientCase.LastViewedAt,
            clientCase.ActivityConfirmedAt,
            lastTaskAt,
            lastTaskCommentAt,
            lastDocumentAt,
            lastCourtEventAt);

    private static DateTime ResolveLastCaseActivity(
        DateTime createdAt,
        DateTime? lastViewedAt,
        DateTime? activityConfirmedAt,
        DateTime? lastTaskAt,
        DateTime? lastTaskCommentAt,
        DateTime? lastDocumentAt,
        DateTime? lastCourtEventAt)
        => CaseMeaningfulActivityResolver.Resolve(
            new CaseMeaningfulActivitySignals(
                createdAt,
                LastTaskAt: lastTaskAt,
                LastTaskCommentAt: lastTaskCommentAt,
                LastDocumentAt: lastDocumentAt,
                LastCourtEventAt: lastCourtEventAt,
                LastResponsibleViewAt: lastViewedAt,
                LastActivityConfirmationAt: activityConfirmedAt))
            .At;

    private async Task<bool> HasAvailableAutomationUserAsync(
        int userId,
        HashSet<int> vacationUserIds,
        CancellationToken ct)
    {
        if (userId <= 0 || vacationUserIds.Contains(userId))
        {
            return false;
        }

        return await _db.Users
            .AsNoTracking()
            .AnyAsync(user => user.Id == userId && user.IsActive && user.IsApproved, ct);
    }

    private static TaskClientBinding BuildCaseBinding(ClientCase clientCase)
    {
        if (clientCase.ClientId.HasValue && clientCase.Client is not null)
        {
            return TaskClientBinding.Valid(
                clientCase.ClientId.Value,
                clientCase.Client.Name,
                null,
                string.Empty,
                clientCase.Id,
                clientCase.Title);
        }

        if (clientCase.LegalEntityId.HasValue && clientCase.LegalEntity is not null)
        {
            return TaskClientBinding.Valid(
                null,
                string.Empty,
                clientCase.LegalEntityId.Value,
                GetLegalEntityName(clientCase.LegalEntity),
                clientCase.Id,
                clientCase.Title);
        }

        return TaskClientBinding.Invalid("У дела нет владельца.");
    }

    private static TaskClientBinding BuildCaseBinding(CaseInactivityAutomationCaseRow clientCase)
    {
        if (clientCase.ClientId.HasValue)
        {
            return TaskClientBinding.Valid(
                clientCase.ClientId.Value,
                clientCase.ClientName,
                null,
                string.Empty,
                clientCase.Id,
                clientCase.Title);
        }

        if (clientCase.LegalEntityId.HasValue)
        {
            return TaskClientBinding.Valid(
                null,
                string.Empty,
                clientCase.LegalEntityId.Value,
                clientCase.LegalEntityName,
                clientCase.Id,
                clientCase.Title);
        }

        return TaskClientBinding.Invalid("У дела нет владельца.");
    }

    private static string GetLegalEntityName(LegalEntity? entity)
        => entity is null
            ? string.Empty
            : GetLegalEntityName(entity.Id, entity.ShortName, entity.FullName);

    private static string GetLegalEntityName(int id, string? shortName, string? fullName)
        => !string.IsNullOrWhiteSpace(shortName)
            ? shortName.Trim()
            : !string.IsNullOrWhiteSpace(fullName)
                ? fullName.Trim()
                : $"Юрлицо #{id}";

    private static string? ValidateRequest(UpsertTaskJournalItemRequest request)
    {
        var hasClient = request.ClientId is > 0;
        var hasLegalEntity = request.LegalEntityId is > 0;
        if (request.IsInternal)
        {
            if (hasClient || hasLegalEntity || request.ClientCaseId is > 0)
            {
                return "Для внутренней задачи не выбирайте контрагента или дело.";
            }
        }
        else if (hasClient == hasLegalEntity)
        {
            return "Укажите одного контрагента задачи: клиента или юрлицо.";
        }

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return "Укажите наименование задачи.";
        }

        if (request.DueAt == default)
        {
            return "Укажите дату и время исполнения.";
        }

        if (request.ApprovalDueAt.HasValue && request.ApprovalDueAt.Value == default)
        {
            return "Укажите корректный срок согласования.";
        }

        if (request.ContractorPaymentEnabled)
        {
            if (NormalizeMoneyAmount(request.ContractorPaymentAmount) is null)
            {
                return "Укажите сумму оплаты исполнителю.";
            }

            var hasPaymentDate = NormalizeDateOnly(request.ContractorPaymentDueDate) is not null;
            if (request.ContractorPaymentOnResult && hasPaymentDate)
            {
                return "Укажите либо точную дату оплаты, либо оплату по результату. Одновременно эти поля заполнять нельзя.";
            }

            if (request.ContractorPaymentOnResult)
            {
                if (NormalizeContractorPaymentResultDelayDays(request.ContractorPaymentResultDelayDays) is null)
                {
                    return "Укажите количество дней для оплаты по результату.";
                }
            }
            else if (!hasPaymentDate)
            {
                return "Укажите дату оплаты исполнителю.";
            }
        }

        var changeComment = NormalizeOptional(request.ChangeComment);
        if (changeComment is { Length: > 4000 })
        {
            return "Комментарий к изменению не должен превышать 4000 символов.";
        }

        if (request.RecurrenceEnabled)
        {
            var recurrenceValidation = TaskRecurrencePolicy.Validate(BuildRecurrenceRule(request));
            if (recurrenceValidation is not null)
            {
                return recurrenceValidation;
            }
        }

        return null;
    }

    private static TaskRecurrenceRule? BuildRecurrenceRule(UpsertTaskJournalItemRequest request)
    {
        if (!request.RecurrenceEnabled)
        {
            return null;
        }

        return new TaskRecurrenceRule(
            request.RecurrenceFrequency?.Trim().ToLowerInvariant() ?? string.Empty,
            request.RecurrenceInterval ?? 1,
            request.RecurrenceDayOfWeek,
            request.RecurrenceDayOfMonth,
            request.RecurrenceMonth,
            request.RecurrenceTimeMinutes ?? 9 * 60,
            request.RecurrenceSkipWeekends,
            string.IsNullOrWhiteSpace(request.RecurrenceTimeZoneId)
                ? CalendarTimeZoneHelper.DefaultTimeZoneId
                : request.RecurrenceTimeZoneId.Trim());
    }

    private static TaskRecurrenceRule? BuildRecurrenceRule(TaskJournalItem entity)
    {
        if (!entity.RecurrenceEnabled)
        {
            return null;
        }

        return new TaskRecurrenceRule(
            entity.RecurrenceFrequency ?? string.Empty,
            entity.RecurrenceInterval,
            entity.RecurrenceDayOfWeek,
            entity.RecurrenceDayOfMonth,
            entity.RecurrenceMonth,
            entity.RecurrenceTimeMinutes ?? 9 * 60,
            entity.RecurrenceSkipWeekends,
            string.IsNullOrWhiteSpace(entity.RecurrenceTimeZoneId)
                ? CalendarTimeZoneHelper.DefaultTimeZoneId
                : entity.RecurrenceTimeZoneId.Trim());
    }

    private static void ApplyRecurrenceConfiguration(
        TaskJournalItem entity,
        UpsertTaskJournalItemRequest request,
        DateTime afterUtc)
    {
        var rule = BuildRecurrenceRule(request);
        entity.RecurrenceEnabled = rule is not null;
        entity.RecurrenceFrequency = rule?.Frequency;
        entity.RecurrenceInterval = rule?.Interval ?? 1;
        entity.RecurrenceDayOfWeek = rule?.DayOfWeek;
        entity.RecurrenceDayOfMonth = rule?.DayOfMonth;
        entity.RecurrenceMonth = rule?.Month;
        entity.RecurrenceTimeMinutes = rule?.TimeMinutes;
        entity.RecurrenceSkipWeekends = rule?.SkipWeekends ?? false;
        entity.RecurrenceTimeZoneId = rule?.TimeZoneId;
        entity.RecurrenceNextAt = rule is null ? null : TaskRecurrencePolicy.CalculateNextUtc(rule, afterUtc);
    }

    private async Task<TaskJournalItem?> CreateNextRecurringTaskIfNeededAsync(
        TaskJournalItem source,
        TaskAccess access,
        CancellationToken ct)
    {
        var rule = BuildRecurrenceRule(source);
        if (rule is null || TaskRecurrencePolicy.Validate(rule) is not null)
        {
            return null;
        }

        var alreadyCreated = await _db.TaskJournalItems
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(item => item.RecurrencePreviousTaskId == source.Id, ct);
        if (alreadyCreated)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var scheduledAt = source.RecurrenceNextAt.HasValue && source.RecurrenceNextAt.Value > now
            ? CalendarTimeZoneHelper.NormalizeUtcInstant(source.RecurrenceNextAt.Value)
            : TaskRecurrencePolicy.CalculateNextUtc(rule, now);
        var nextTask = new TaskJournalItem
        {
            ClientId = source.ClientId,
            LegalEntityId = source.LegalEntityId,
            ClientCaseId = source.ClientCaseId,
            ClientBillingClassification = source.ClientBillingClassification,
            BillingContractId = source.BillingContractId,
            BillingModeSnapshot = source.BillingModeSnapshot,
            BillingDecisionAt = now,
            ObjectName = source.ObjectName,
            Title = source.Title,
            Description = source.Description,
            AssigneeUserId = source.AssigneeUserId,
            AssigneeName = source.AssigneeName,
            AuthorUserId = source.AuthorUserId,
            AuthorName = source.AuthorName,
            DueAt = scheduledAt,
            ApprovalDueAt = null,
            PlannedDurationMinutes = source.PlannedDurationMinutes,
            // Повторение создает рабочую задачу, но не должно автоматически создавать
            // новое финансовое обязательство перед исполнителем.
            ContractorPaymentEnabled = false,
            ContractorPaymentAmount = null,
            ContractorPaymentDueDate = null,
            ContractorPaymentOnResult = false,
            ContractorPaymentResultDelayDays = null,
            Type = source.Type,
            Priority = source.Priority,
            Status = "new",
            IsUnderControl = source.IsUnderControl,
            ControlMarkedAt = source.IsUnderControl ? now : null,
            ControlMarkedByUserId = source.IsUnderControl ? access.UserId : null,
            ControlMarkedByUserName = source.IsUnderControl ? access.DisplayName : string.Empty,
            Result = null,
            RemindersJson = source.RemindersJson,
            ChannelsJson = source.ChannelsJson,
            TagsJson = source.TagsJson,
            RecurrenceEnabled = true,
            RecurrenceFrequency = rule.Frequency,
            RecurrenceInterval = rule.Interval,
            RecurrenceDayOfWeek = rule.DayOfWeek,
            RecurrenceDayOfMonth = rule.DayOfMonth,
            RecurrenceMonth = rule.Month,
            RecurrenceTimeMinutes = rule.TimeMinutes,
            RecurrenceSkipWeekends = rule.SkipWeekends,
            RecurrenceTimeZoneId = rule.TimeZoneId,
            RecurrenceNextAt = TaskRecurrencePolicy.CalculateNextUtc(rule, scheduledAt),
            RecurrencePreviousTaskId = source.Id,
            RecurrenceSequence = source.RecurrenceSequence + 1,
            CreatedAt = now,
            UpdatedAt = now,
            Participants = source.Participants.Select(participant => new TaskJournalParticipant
            {
                UserId = participant.UserId,
                Role = participant.Role,
                DisplayName = participant.DisplayName,
                Email = participant.Email,
                SortOrder = participant.SortOrder,
                ApprovalStatus = "pending",
                Resolution = null,
                RespondedAt = null,
                CreatedAt = now,
                UpdatedAt = now,
            }).ToList(),
        };

        AddTaskHistory(nextTask, access, $"Задача создана автоматически как повторение задачи #{source.Id}.");
        AddTaskHistory(source, access, $"После завершения создано следующее повторение со сроком {FormatHistoryDateTime(scheduledAt)}.");
        _db.TaskJournalItems.Add(nextTask);
        return nextTask;
    }

    private async Task NotifyRecurringTaskCreatedAsync(
        TaskJournalItem? task,
        TaskAccess access,
        CancellationToken ct)
    {
        if (task is null)
        {
            return;
        }

        await _notificationService.NotifyTaskAssignedAsync(task, access.DisplayName, access.UserId, ct);
        await NotifyTaskScheduleConflictIfNeededAsync(task, access, ct);
        await SafeSyncExternalCalendarTaskAsync(task.Id, ct);
    }

    private static string? ValidateTemplateRequest(UpsertTaskJournalTemplateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return "Укажите название шаблона задачи.";
        }

        if (request.Name.Trim().Length > 200)
        {
            return "Название шаблона не должно превышать 200 символов.";
        }

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return "Укажите наименование задачи в шаблоне.";
        }

        if (request.Title.Trim().Length > 500)
        {
            return "Наименование задачи в шаблоне не должно превышать 500 символов.";
        }

        if (NormalizeEnumValue(request.Type, AllowedTypes, string.Empty) == string.Empty)
        {
            return "Некорректный тип задачи в шаблоне.";
        }

        if (NormalizeEnumValue(request.Priority, AllowedPriorities, string.Empty) == string.Empty)
        {
            return "Некорректный приоритет задачи в шаблоне.";
        }

        if (NormalizeEnumValue(request.BindingType, AllowedTemplateBindingTypes, string.Empty) == string.Empty)
        {
            return "Некорректная привязка шаблона задачи.";
        }

        var bindingType = NormalizeEnumValue(request.BindingType, AllowedTemplateBindingTypes, "none");
        if (!string.Equals(bindingType, "none", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(request.BindingKey))
        {
            return "Укажите этап или тип дела для привязанного шаблона.";
        }

        return null;
    }

    private async Task<IReadOnlyDictionary<int, IReadOnlyList<TaskJournalDeadlineChangeRequest>>> LoadActiveDeadlineChangeRequestsAsync(
        IReadOnlyCollection<int> taskIds,
        CancellationToken ct)
    {
        if (taskIds.Count == 0)
        {
            return new Dictionary<int, IReadOnlyList<TaskJournalDeadlineChangeRequest>>();
        }

        var requests = await _db.TaskJournalDeadlineChangeRequests
            .AsNoTracking()
            .Where(item => taskIds.Contains(item.TaskJournalItemId) && item.Status == "pending")
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .ToListAsync(ct);

        return requests
            .GroupBy(item => item.TaskJournalItemId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<TaskJournalDeadlineChangeRequest>)group.ToList());
    }

    private static TaskJournalItemDto Map(
        TaskJournalItem entity,
        TaskClientBinding? binding = null,
        IReadOnlyList<TaskJournalDeadlineChangeRequest>? activeDeadlineChangeRequests = null)
        => new()
        {
            Id = entity.Id,
            ClientId = entity.ClientId,
            ClientName = binding?.ClientName ?? entity.Client?.Name ?? string.Empty,
            LegalEntityId = entity.LegalEntityId,
            LegalEntityName = binding?.LegalEntityName ?? GetLegalEntityName(entity.LegalEntity),
            IsInternal = !entity.ClientId.HasValue && !entity.LegalEntityId.HasValue,
            ClientCaseId = entity.ClientCaseId,
            ClientCaseTitle = binding?.ClientCaseTitle ?? entity.ClientCase?.Title ?? string.Empty,
            ClientBillingClassification = entity.ClientBillingClassification,
            BillingContractId = entity.BillingContractId,
            BillingContractNumber = entity.BillingContract?.Number ?? string.Empty,
            BillingMode = entity.BillingModeSnapshot ?? string.Empty,
            BillingInvoiceId = entity.BillingInvoiceId,
            BillingInvoiceNumber = entity.BillingInvoice?.Account ?? string.Empty,
            BillingStatus = ResolveTaskBillingStatus(entity),
            ObjectName = entity.ObjectName,
            Title = entity.Title,
            Description = entity.Description ?? string.Empty,
            AssigneeUserId = entity.AssigneeUserId,
            AssigneeName = entity.AssigneeName,
            AuthorUserId = entity.AuthorUserId,
            AuthorName = entity.AuthorName,
            DueAt = ToIso(entity.DueAt),
            ApprovalDueAt = entity.ApprovalDueAt.HasValue ? ToIso(entity.ApprovalDueAt.Value) : null,
            PlannedDurationMinutes = entity.PlannedDurationMinutes,
            ContractorPaymentEnabled = entity.ContractorPaymentEnabled,
            ContractorPaymentAmount = entity.ContractorPaymentAmount,
            ContractorPaymentDueDate = entity.ContractorPaymentDueDate.HasValue ? ToIso(entity.ContractorPaymentDueDate.Value) : null,
            ContractorPaymentOnResult = entity.ContractorPaymentOnResult,
            ContractorPaymentResultDelayDays = entity.ContractorPaymentResultDelayDays,
            ContractorPaymentId = entity.ContractorPaymentId,
            ContractorPaymentStatus = entity.ContractorPayment?.Status.ToString() ?? string.Empty,
            Type = entity.Type,
            Priority = entity.Priority,
            Status = ResolveTaskStatus(entity),
            IsUnderControl = entity.IsUnderControl,
            ControlMarkedAt = entity.ControlMarkedAt.HasValue ? ToIso(entity.ControlMarkedAt.Value) : null,
            ControlMarkedByUserId = entity.ControlMarkedByUserId,
            ControlMarkedByUserName = entity.ControlMarkedByUserName,
            Result = entity.Result ?? string.Empty,
            Reminders = DeserializeList(entity.RemindersJson),
            Channels = DeserializeList(entity.ChannelsJson),
            Tags = DeserializeList(entity.TagsJson),
            RecurrenceEnabled = entity.RecurrenceEnabled,
            RecurrenceFrequency = entity.RecurrenceFrequency,
            RecurrenceInterval = entity.RecurrenceInterval,
            RecurrenceDayOfWeek = entity.RecurrenceDayOfWeek,
            RecurrenceDayOfMonth = entity.RecurrenceDayOfMonth,
            RecurrenceMonth = entity.RecurrenceMonth,
            RecurrenceTimeMinutes = entity.RecurrenceTimeMinutes,
            RecurrenceSkipWeekends = entity.RecurrenceSkipWeekends,
            RecurrenceTimeZoneId = entity.RecurrenceTimeZoneId,
            RecurrenceNextAt = entity.RecurrenceNextAt.HasValue ? ToIso(entity.RecurrenceNextAt.Value) : null,
            RecurrencePreviousTaskId = entity.RecurrencePreviousTaskId,
            RecurrenceSequence = entity.RecurrenceSequence,
            Participants = entity.Participants
                .OrderBy(ParticipantRoleOrder)
                .ThenBy(participant => participant.SortOrder)
                .ThenBy(participant => participant.Id)
                .Select(participant => new TaskJournalParticipantDto
                {
                    Id = participant.Id,
                    UserId = participant.UserId,
                    Role = participant.Role,
                    DisplayName = participant.DisplayName,
                    Email = participant.Email,
                    SortOrder = participant.SortOrder,
                    ApprovalStatus = participant.ApprovalStatus,
                    Resolution = participant.Resolution ?? string.Empty,
                    RespondedAt = participant.RespondedAt.HasValue ? ToIso(participant.RespondedAt.Value) : null,
                })
                .ToList(),
            ActiveDeadlineChangeRequests = (activeDeadlineChangeRequests ?? entity.DeadlineChangeRequests
                    .Where(request => string.Equals(request.Status, "pending", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(request => request.CreatedAt)
                    .ThenByDescending(request => request.Id)
                    .ToList())
                .Select(MapDeadlineChangeRequest)
                .ToList(),
            CreatedAt = ToIso(entity.CreatedAt),
            UpdatedAt = entity.UpdatedAt.HasValue ? ToIso(entity.UpdatedAt.Value) : null,
        };

    private static TaskJournalTemplateDto MapTemplate(TaskJournalTemplate entity)
        => new()
        {
            Id = entity.Id,
            Name = entity.Name,
            Description = entity.Description ?? string.Empty,
            Title = entity.Title,
            TaskDescription = entity.TaskDescription ?? string.Empty,
            Type = entity.Type,
            Priority = entity.Priority,
            PlannedDurationMinutes = entity.PlannedDurationMinutes,
            DueOffsetMinutes = entity.DueOffsetMinutes,
            AssigneeUserId = entity.AssigneeUserId,
            AssigneeName = entity.AssigneeName,
            BindingType = entity.BindingType,
            BindingKey = entity.BindingKey,
            IsActive = entity.IsActive,
            Reminders = DeserializeList(entity.RemindersJson),
            Channels = DeserializeList(entity.ChannelsJson),
            Tags = DeserializeList(entity.TagsJson),
            Participants = entity.Participants
                .OrderBy(participant => ParticipantRoleOrder(participant.Role))
                .ThenBy(participant => participant.SortOrder)
                .ThenBy(participant => participant.Id)
                .Select(participant => new TaskJournalTemplateParticipantDto
                {
                    Id = participant.Id,
                    UserId = participant.UserId,
                    Role = participant.Role,
                    DisplayName = participant.DisplayName,
                    Email = participant.Email,
                    SortOrder = participant.SortOrder,
                })
                .ToList(),
            CreatedByUserId = entity.CreatedByUserId,
            CreatedByName = entity.CreatedByName,
            CreatedAt = ToIso(entity.CreatedAt),
            UpdatedAt = entity.UpdatedAt.HasValue ? ToIso(entity.UpdatedAt.Value) : null,
        };

    private static string ResolveTaskStatus(TaskJournalItem entity)
    {
        if (string.Equals(entity.Status, "done", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.Status, "cancelled", StringComparison.OrdinalIgnoreCase))
        {
            return entity.Status;
        }

        var approvers = entity.Participants
            .Where(participant => string.Equals(participant.Role, "approver", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var allApproved = approvers.Count > 0
            && approvers.All(participant => string.Equals(participant.ApprovalStatus, "approved", StringComparison.OrdinalIgnoreCase));

        if (allApproved)
        {
            return "approved";
        }

        if ((string.Equals(entity.Status, "onApproval", StringComparison.OrdinalIgnoreCase)
                || string.Equals(entity.Status, "overdueApproval", StringComparison.OrdinalIgnoreCase))
            && GetApprovalDueAt(entity) < DateTime.UtcNow)
        {
            return "overdueApproval";
        }

        return entity.Status;
    }

    private static bool IsTaskDueOverdue(TaskJournalItem entity, DateTime asOfUtc)
    {
        var status = ResolveTaskStatus(entity);
        return ActiveTaskStatuses.Contains(status)
            && entity.DueAt < asOfUtc;
    }

    private static DateTime GetApprovalDueAt(TaskJournalItem entity)
        => entity.ApprovalDueAt ?? entity.DueAt;

    private static string ToIso(DateTime value)
        => DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("O");

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeCompletionResult(string status, string? result)
        => NormalizeOptional(result);

    private static DateTime NormalizeUtcInstant(DateTime value)
        => CalendarTimeZoneHelper.NormalizeUtcInstant(value);

    private static DateTime? NormalizeApprovalDueAt(DateTime? value)
        => value.HasValue && value.Value != default ? NormalizeUtcInstant(value.Value) : null;

    private static DateTime? ResolveApprovalDueAt(
        string status,
        DateTime? requestedApprovalDueAt,
        int approvalDueDays,
        DateTime baseUtc)
    {
        var normalized = NormalizeApprovalDueAt(requestedApprovalDueAt);
        if (normalized.HasValue || !IsApprovalActiveStatus(status))
        {
            return normalized;
        }

        return baseUtc.AddDays(Math.Clamp(approvalDueDays, 1, 365));
    }

    private static string? ValidateRevisionDueAt(string? status, DateTime dueAt, DateTime nowUtc)
        => string.Equals(status, "revision", StringComparison.OrdinalIgnoreCase) && dueAt <= nowUtc
            ? "Укажите новый срок доработки позже текущего времени."
            : null;

    private static decimal? NormalizeMoneyAmount(decimal? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        var normalized = Math.Round(value.Value, 2, MidpointRounding.AwayFromZero);
        return normalized > 0m ? normalized : null;
    }

    private static DateTime? NormalizeDateOnly(DateTime? value)
        => value.HasValue && value.Value != default ? value.Value.Date : null;

    private static int? NormalizeContractorPaymentResultDelayDays(int? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        return value.Value is >= 0 and <= 365 ? value.Value : null;
    }

    private async Task<string?> ValidateContractorPaymentRequestAsync(
        UpsertTaskJournalItemRequest request,
        int? assigneeUserId,
        CancellationToken ct)
    {
        if (!request.ContractorPaymentEnabled)
        {
            return null;
        }

        if (!assigneeUserId.HasValue)
        {
            return "Для оплаты исполнителю выберите исполнителя из списка пользователей.";
        }

        var isExternalContractor = await _db.Users
            .AsNoTracking()
            .Where(user => user.Id == assigneeUserId.Value)
            .Select(user => user.IsExternalContractor)
            .FirstOrDefaultAsync(ct);

        return isExternalContractor
            ? null
            : "В профиле выбранного исполнителя не включен признак привлеченного исполнителя.";
    }

    private static string NormalizeEnumValue(string? value, HashSet<string> allowedValues, string fallback)
    {
        var normalized = NormalizeOptional(value);
        if (normalized is null)
        {
            return fallback;
        }

        return allowedValues.FirstOrDefault(allowed => string.Equals(allowed, normalized, StringComparison.OrdinalIgnoreCase))
            ?? fallback;
    }

    private static int NormalizeTaskDuration(int? value)
    {
        var minutes = value.GetValueOrDefault(60);
        minutes = Math.Clamp(minutes, 15, 8 * 60);
        return (int)Math.Round(minutes / 15m, MidpointRounding.AwayFromZero) * 15;
    }

    private static int NormalizeTemplateDueOffsetMinutes(int? value)
    {
        var minutes = value.GetValueOrDefault(1440);
        minutes = Math.Clamp(minutes, 0, 365 * 24 * 60);
        return (int)Math.Round(minutes / 15m, MidpointRounding.AwayFromZero) * 15;
    }

    private static string NormalizeTemplateBindingKey(string? bindingType, string? bindingKey)
    {
        var normalizedBindingType = NormalizeEnumValue(bindingType, AllowedTemplateBindingTypes, "none");
        if (string.Equals(normalizedBindingType, "none", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return (NormalizeOptional(bindingKey) ?? string.Empty).Length > 120
            ? (NormalizeOptional(bindingKey) ?? string.Empty)[..120]
            : NormalizeOptional(bindingKey) ?? string.Empty;
    }

    private async Task SyncContractorPaymentAsync(TaskJournalItem entity, DateTime? resultBaseDate, CancellationToken ct)
    {
        var payment = await ResolveContractorPaymentAsync(entity, ct);
        var shouldHavePayment = await ShouldCreateOrKeepContractorPaymentAsync(entity, ct);

        if (!shouldHavePayment)
        {
            if (payment is not null && CanChangeGeneratedContractorPayment(payment))
            {
                payment.Status = PaymentStatus.Cancelled;
                payment.IsPaid = false;
                payment.PaidAmount = 0m;
                payment.PaidDate = null;
                payment.LastPaymentDate = null;
                payment.SystemNotes = AppendContractorPaymentNote(payment.SystemNotes, "Автоматический платеж отменен из карточки задачи.");
                entity.ContractorPayment = payment;
                entity.ContractorPaymentId = payment.Id;
            }

            return;
        }

        var dueDate = ResolveContractorPaymentDueDate(entity, payment, resultBaseDate);
        if (dueDate is null)
        {
            return;
        }

        var amount = NormalizeMoneyAmount(entity.ContractorPaymentAmount)!.Value;
        var description = BuildContractorPaymentDescription(entity);
        var systemNotes = BuildContractorPaymentSystemNotes(entity);
        var assigneeName = string.IsNullOrWhiteSpace(entity.AssigneeName) ? "Исполнитель" : entity.AssigneeName.Trim();

        if (payment is null)
        {
            payment = new Payment
            {
                Type = PaymentType.Expense,
                Status = PaymentStatus.Pending,
                Amount = amount,
                PaidAmount = 0m,
                Date = dueDate.Value,
                PlannedDate = dueDate.Value,
                Description = description,
                CounterpartyName = assigneeName,
                Notes = "Автоматически создано из задачи.",
                SystemNotes = systemNotes,
                ClientId = null,
                LegalEntityId = null,
                ExpenseClientId = entity.ClientId,
                ExpenseLegalEntityId = entity.LegalEntityId,
                ClientCaseId = entity.ClientCaseId,
                DocumentKind = DocumentKind.Payment,
                InvoiceWorkflowStatus = InvoiceWorkflowStatus.Draft,
                CreatedAt = DateTime.UtcNow,
                IsPaid = false,
            };

            if (entity.AssigneeUserId.HasValue)
            {
                payment.Responsibles.Add(new PaymentResponsible
                {
                    UserId = entity.AssigneeUserId.Value,
                });
            }

            _db.Payments.Add(payment);
            entity.ContractorPayment = payment;
            return;
        }

        if (!CanChangeGeneratedContractorPayment(payment))
        {
            return;
        }

        payment.Type = PaymentType.Expense;
        payment.Status = PaymentStatus.Pending;
        payment.Amount = amount;
        payment.Date = dueDate.Value;
        payment.PlannedDate = dueDate.Value;
        payment.Description = description;
        payment.CounterpartyName = assigneeName;
        payment.SystemNotes = systemNotes;
        payment.ExpenseClientId = entity.ClientId;
        payment.ExpenseLegalEntityId = entity.LegalEntityId;
        payment.ClientId = null;
        payment.LegalEntityId = null;
        payment.ClientCaseId = entity.ClientCaseId;
        payment.IsPaid = false;
        payment.PaidAmount = 0m;
        payment.PaidDate = null;
        payment.LastPaymentDate = null;

        if (entity.AssigneeUserId.HasValue && !payment.Responsibles.Any(item => item.UserId == entity.AssigneeUserId.Value))
        {
            payment.Responsibles.Add(new PaymentResponsible
            {
                PaymentId = payment.Id,
                UserId = entity.AssigneeUserId.Value,
            });
        }

        entity.ContractorPayment = payment;
        entity.ContractorPaymentId = payment.Id;
    }

    private async Task<Payment?> ResolveContractorPaymentAsync(TaskJournalItem entity, CancellationToken ct)
    {
        if (entity.ContractorPayment is not null)
        {
            var responsibles = _db.Entry(entity.ContractorPayment).Collection(payment => payment.Responsibles);
            if (!responsibles.IsLoaded)
            {
                await responsibles.LoadAsync(ct);
            }

            return entity.ContractorPayment;
        }

        if (entity.ContractorPaymentId.HasValue)
        {
            var linked = await _db.Payments
                .Include(payment => payment.Responsibles)
                .FirstOrDefaultAsync(payment => payment.Id == entity.ContractorPaymentId.Value, ct);
            if (linked is not null)
            {
                entity.ContractorPayment = linked;
                return linked;
            }
        }

        var marker = BuildContractorPaymentMarker(entity.Id);
        var existing = await _db.Payments
            .Include(payment => payment.Responsibles)
            .FirstOrDefaultAsync(payment => payment.SystemNotes != null && payment.SystemNotes.Contains(marker), ct);
        if (existing is not null)
        {
            entity.ContractorPayment = existing;
            entity.ContractorPaymentId = existing.Id;
        }

        return existing;
    }

    private async Task<bool> ShouldCreateOrKeepContractorPaymentAsync(TaskJournalItem entity, CancellationToken ct)
    {
        if (!entity.ContractorPaymentEnabled
            || !entity.AssigneeUserId.HasValue
            || NormalizeMoneyAmount(entity.ContractorPaymentAmount) is null)
        {
            return false;
        }

        if (entity.ContractorPaymentOnResult)
        {
            if (NormalizeContractorPaymentResultDelayDays(entity.ContractorPaymentResultDelayDays) is null)
            {
                return false;
            }

            if (!IsContractorPaymentResultTriggerStatus(entity.Status))
            {
                return false;
            }
        }
        else if (NormalizeDateOnly(entity.ContractorPaymentDueDate) is null)
        {
            return false;
        }

        return await _db.Users
            .AsNoTracking()
            .AnyAsync(user => user.Id == entity.AssigneeUserId.Value && user.IsExternalContractor, ct);
    }

    private static bool IsContractorPaymentResultTriggerStatus(string? status)
        => string.Equals(status, "done", StringComparison.OrdinalIgnoreCase);

    private async Task<DateTime?> ResolveContractorPaymentResultBaseDateAsync(
        string? oldStatus,
        string? nextStatus,
        DateTime? changedAt,
        CancellationToken ct)
    {
        if (!IsContractorPaymentResultTriggerStatus(nextStatus)
            || string.Equals(oldStatus, nextStatus, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct);
        return CalendarTimeZoneHelper.ToUserLocal(changedAt ?? DateTime.UtcNow, timeZone).Date;
    }

    private static DateTime? ResolveContractorPaymentDueDate(TaskJournalItem entity, Payment? payment, DateTime? resultBaseDate)
    {
        if (!entity.ContractorPaymentOnResult)
        {
            return NormalizeDateOnly(entity.ContractorPaymentDueDate);
        }

        var delayDays = NormalizeContractorPaymentResultDelayDays(entity.ContractorPaymentResultDelayDays);
        if (delayDays is null)
        {
            return null;
        }

        if (resultBaseDate is null && payment is not null && payment.Date != default)
        {
            return payment.Date.Date;
        }

        var baseDate = (resultBaseDate ?? entity.UpdatedAt ?? entity.CreatedAt).Date;
        return baseDate.AddDays(delayDays.Value);
    }

    private static bool CanChangeGeneratedContractorPayment(Payment payment)
        => payment.PaidAmount <= 0m
           && !payment.IsPaid
           && payment.Status is not PaymentStatus.Completed;

    private static string BuildContractorPaymentMarker(int taskId)
        => $"{ContractorPaymentMarker}:{taskId}";

    private static string BuildContractorPaymentSystemNotes(TaskJournalItem entity)
        => string.Join('\n', new[]
        {
            BuildContractorPaymentMarker(entity.Id),
            "Автоматический платеж исполнителю из задачи.",
        });

    private static string BuildContractorPaymentDescription(TaskJournalItem entity)
    {
        var title = string.IsNullOrWhiteSpace(entity.Title) ? $"задаче #{entity.Id}" : $"задаче «{entity.Title.Trim()}»";
        return Truncate($"Оплата исполнителю по {title}", 500);
    }

    private static string AppendContractorPaymentNote(string? currentNotes, string note)
    {
        var normalized = NormalizeOptional(currentNotes);
        return string.IsNullOrWhiteSpace(normalized)
            ? note
            : $"{normalized}\n{note}";
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private async Task<CalendarSyncSettingsDto> LoadCalendarSyncSettingsAsync(CancellationToken ct)
    {
        var setting = await _db.SystemSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Key == CalendarSyncSettingsKey, ct);
        if (setting is null || string.IsNullOrWhiteSpace(setting.Value))
        {
            return CalendarSyncProviderRegistry.BuildDefaultSettings();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<CalendarSyncSettingsDto>(setting.Value, JsonOptions);
            return settings is null
                ? CalendarSyncProviderRegistry.BuildDefaultSettings()
                : CalendarSyncProviderRegistry.NormalizeSettings(settings);
        }
        catch (JsonException)
        {
            return CalendarSyncProviderRegistry.BuildDefaultSettings();
        }
    }

    private static string BuildIcsCalendar(IEnumerable<TaskJournalItem> tasks, IReadOnlyList<int>? reminderMinutes = null, TimeZoneInfo? timeZone = null)
    {
        var now = DateTime.UtcNow;
        var reminders = reminderMinutes is { Count: > 0 } ? reminderMinutes : IcsReminderOptions.DefaultReminderMinutes;
        var calendarTimeZone = timeZone ?? CalendarTimeZoneHelper.ResolveTimeZone(null);
        var builder = new StringBuilder();
        builder.AppendLine("BEGIN:VCALENDAR");
        builder.AppendLine("VERSION:2.0");
        builder.AppendLine("PRODID:-//PayPlanner//Task Journal//RU");
        builder.AppendLine("CALSCALE:GREGORIAN");
        builder.AppendLine("METHOD:PUBLISH");
        builder.AppendLine("X-WR-CALNAME:Журнал задач");

        foreach (var task in tasks)
        {
            var start = CalendarTimeZoneHelper.ToExternalUtc(task.DueAt, calendarTimeZone);
            var end = start.AddMinutes(NormalizeTaskDuration(task.PlannedDurationMinutes));
            var description = string.Join("\\n", new[]
            {
                task.Client?.Name,
                GetLegalEntityName(task.LegalEntity),
                task.ClientCase?.Title,
                TaskStatusLabel(task.Status),
                task.Description,
            }.Where(value => !string.IsNullOrWhiteSpace(value)));

            builder.AppendLine("BEGIN:VEVENT");
            builder.AppendLine($"UID:payplanner-task-{task.Id}@crm.elitpravo.com");
            builder.AppendLine($"DTSTAMP:{FormatIcsDate(now, calendarTimeZone)}");
            builder.AppendLine($"DTSTART:{FormatIcsDate(start, calendarTimeZone)}");
            builder.AppendLine($"DTEND:{FormatIcsDate(end, calendarTimeZone)}");
            builder.AppendLine($"SUMMARY:{EscapeIcsText(task.Title)}");
            builder.AppendLine($"DESCRIPTION:{EscapeIcsText(description)}");
            builder.AppendLine($"STATUS:{(task.Status == "done" ? "COMPLETED" : "CONFIRMED")}");
            foreach (var minutes in reminders)
            {
                AppendIcsAlarm(builder, task, IcsReminderOptions.ToTriggerDuration(minutes));
            }
            builder.AppendLine("END:VEVENT");
        }

        builder.AppendLine("END:VCALENDAR");
        return builder.ToString().ReplaceLineEndings("\r\n");
    }

    private static void AppendIcsAlarm(StringBuilder builder, TaskJournalItem task, string triggerBeforeStart)
    {
        builder.AppendLine("BEGIN:VALARM");
        builder.AppendLine("ACTION:DISPLAY");
        builder.AppendLine($"DESCRIPTION:{EscapeIcsText($"Напоминание: {task.Title}")}");
        builder.AppendLine($"TRIGGER:-{triggerBeforeStart}");
        builder.AppendLine("END:VALARM");
    }

    private static string FormatIcsDate(DateTime value, TimeZoneInfo timeZone)
        => CalendarTimeZoneHelper.FormatUtcIcsDateTime(value, timeZone);

    private static string EscapeIcsText(string? value)
        => (value ?? string.Empty)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(";", "\\;", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal)
            .Replace("\r\n", "\\n", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\n", StringComparison.Ordinal);

    private async Task<IReadOnlyDictionary<int, User>> LoadAvailableAutomationUsersAsync(
        IEnumerable<int?> userIds,
        CancellationToken ct)
    {
        var normalizedUserIds = userIds
            .Where(userId => userId.HasValue && userId.Value > 0)
            .Select(userId => userId!.Value)
            .Distinct()
            .ToArray();
        if (normalizedUserIds.Length == 0)
        {
            return new Dictionary<int, User>();
        }

        return await _db.Users
            .AsNoTracking()
            .Where(user => normalizedUserIds.Contains(user.Id) && user.IsActive && user.IsApproved)
            .Select(user => new User
            {
                Id = user.Id,
                FullName = user.FullName,
                Email = user.Email,
            })
            .ToDictionaryAsync(user => user.Id, ct);
    }

    private static User? GetAvailableAutomationUser(
        IReadOnlyDictionary<int, User> availableUsers,
        int? userId)
        => userId.HasValue && availableUsers.TryGetValue(userId.Value, out var user)
            ? user
            : null;

    private static TaskParticipantDraft BuildAutomationAssignee(
        int? responsibleUserId,
        IReadOnlyDictionary<int, User> availableUsers,
        User? configuredDefaultAssignee,
        TaskAccess fallbackAccess)
    {
        var user = GetAvailableAutomationUser(availableUsers, responsibleUserId)
            ?? configuredDefaultAssignee;
        return user is null
            ? BuildAutomationFallbackAssignee(fallbackAccess)
            : new TaskParticipantDraft(
                user.Id,
                BuildUserDisplayName(user.FullName, user.Email),
                user.Email,
                "assignee",
                0,
                "pending",
                null);
    }

    private static int? ResolveAutomationResponsibleUserId(ClientCase clientCase)
        => clientCase.ResponsibleUserId ?? clientCase.Client?.ResponsibleUserId ?? clientCase.LegalEntity?.ResponsibleUserId;

    private static TaskParticipantDraft BuildAutomationFallbackAssignee(TaskAccess fallbackAccess)
        => new(
            fallbackAccess.UserId,
            fallbackAccess.DisplayName,
            string.Empty,
            "assignee",
            0,
            "pending",
            null);

    private async Task<User?> ResolveConfiguredAutomationUserAsync(int? configuredUserId, CancellationToken ct)
    {
        if (!configuredUserId.HasValue || configuredUserId.Value <= 0)
        {
            return null;
        }

        return await _db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(user =>
                user.Id == configuredUserId.Value &&
                user.IsActive &&
                user.IsApproved,
                ct);
    }

    private static string ResolveAutomationPriority(int daysWithoutActiveTask, int inactiveDays, int escalationDays)
    {
        if (daysWithoutActiveTask >= escalationDays)
        {
            return "urgent";
        }

        return daysWithoutActiveTask >= inactiveDays ? "high" : "normal";
    }

    private static string BuildAutomationTaskDescription(bool isEscalated)
        => isEscalated
            ? "Автоконтроль: по делу давно нет активной задачи, сработала эскалация. Срочно проверьте текущее состояние и зафиксируйте следующий шаг."
            : "Автоконтроль: по делу нет активной задачи. Проверьте текущее состояние и зафиксируйте следующий шаг.";

    private static void AddEscalationObserver(TaskJournalItem task, User? escalationUser, int? assigneeUserId, DateTime nowUtc)
    {
        if (escalationUser is null || escalationUser.Id == assigneeUserId)
        {
            return;
        }

        task.Participants.Add(new TaskJournalParticipant
        {
            UserId = escalationUser.Id,
            Role = "observer",
            DisplayName = BuildUserDisplayName(escalationUser.FullName, escalationUser.Email),
            Email = escalationUser.Email,
            SortOrder = task.Participants.Count,
            ApprovalStatus = "pending",
            CreatedAt = nowUtc,
            UpdatedAt = nowUtc,
        });
    }

    private async Task<DateTime> BuildDefaultTaskDueAtUtcAsync(int? userId, CancellationToken ct)
    {
        var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, userId, ct);
        return BuildDefaultTaskDueAtUtc(DateTime.UtcNow, timeZone);
    }

    private static DateTime BuildDefaultTaskDueAtUtc(DateTime nowUtc, TimeZoneInfo timeZone)
    {
        var localNow = CalendarTimeZoneHelper.ToUserLocal(nowUtc, timeZone);
        var localDueAt = localNow.Date.AddDays(1).AddHours(9);
        return CalendarTimeZoneHelper.FromUserLocalToUtc(localDueAt, timeZone);
    }

    private static TaskJournalItem BuildAutomationTaskEntity(
        CaseInactivityAutomationCaseRow clientCase,
        TaskClientBinding binding,
        TaskAccess access,
        TaskParticipantDraft assignee,
        TaskJournalSettingsDto settings,
        User? escalationUser,
        DateTime dueAt,
        DateTime now,
        DateTime? lastTaskAt,
        DateTime? lastTaskCommentAt,
        DateTime? lastDocumentAt,
        DateTime? lastCourtEventAt)
        => BuildAutomationTaskEntity(
            new ClientCase
            {
                Id = clientCase.Id,
                Title = clientCase.Title,
                CreatedAt = clientCase.CreatedAt,
                LastViewedAt = clientCase.LastViewedAt,
                ActivityConfirmedAt = clientCase.ActivityConfirmedAt,
            },
            binding,
            access,
            assignee,
            settings,
            escalationUser,
            dueAt,
            now,
            lastTaskAt,
            lastTaskCommentAt,
            lastDocumentAt,
            lastCourtEventAt);

    private static TaskJournalItem BuildAutomationTaskEntity(
        ClientCase clientCase,
        TaskClientBinding binding,
        TaskAccess access,
        TaskParticipantDraft assignee,
        TaskJournalSettingsDto settings,
        User? escalationUser,
        DateTime dueAt,
        DateTime now,
        DateTime? lastTaskAt,
        DateTime? lastTaskCommentAt,
        DateTime? lastDocumentAt,
        DateTime? lastCourtEventAt)
    {
        var lastActivity = ResolveLastCaseActivity(
            clientCase,
            lastTaskAt,
            lastTaskCommentAt,
            lastDocumentAt,
            lastCourtEventAt);
        var daysWithoutActiveTask = Math.Max(0, (int)Math.Floor((now - lastActivity).TotalDays));
        var isEscalated = daysWithoutActiveTask >= settings.CaseInactivityEscalationDays;

        var task = new TaskJournalItem
        {
            ClientId = binding.ClientId,
            LegalEntityId = binding.LegalEntityId,
            ClientCaseId = clientCase.Id,
            ObjectName = BuildObjectName(binding.OwnerName, clientCase.Title),
            Title = "Проверить движение по делу",
            Description = BuildAutomationTaskDescription(isEscalated),
            AssigneeUserId = assignee.UserId,
            AssigneeName = assignee.DisplayName,
            AuthorUserId = access.UserId,
            AuthorName = access.DisplayName,
            DueAt = dueAt,
            PlannedDurationMinutes = 60,
            Type = "control",
            Priority = ResolveAutomationPriority(daysWithoutActiveTask, settings.CaseInactivityDays, settings.CaseInactivityEscalationDays),
            Status = "new",
            Result = null,
            RemindersJson = SerializeList(["before1d", "atDue"], AllowedReminders, ["atDue"]),
            ChannelsJson = SerializeList(["inApp"], AllowedChannels, ["inApp"]),
            TagsJson = SerializeFreeList([CaseInactivityAutomationTag]),
            InactivityControlCycleNumber = 1,
            InactivityControlNextCheckAt = now.AddDays(CaseInactivityControlStateMachine.FirstRepeatOffsetDays),
            CreatedAt = now,
            UpdatedAt = now,
            Participants = [ToParticipantEntity(assignee)],
        };

        AddEscalationObserver(task, isEscalated ? escalationUser : null, assignee.UserId, now);
        return task;
    }

    private static string SerializeList(IEnumerable<string>? values, HashSet<string> allowedValues, IReadOnlyCollection<string> fallback)
    {
        var normalized = (values ?? Array.Empty<string>())
            .Select(value => NormalizeEnumValue(value, allowedValues, string.Empty))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return JsonSerializer.Serialize(normalized.Count > 0 ? normalized : fallback, JsonOptions);
    }

    private static string SerializeFreeList(IEnumerable<string>? values)
    {
        var normalized = (values ?? Array.Empty<string>())
            .Select(NormalizeOptional)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();

        return JsonSerializer.Serialize(normalized, JsonOptions);
    }

    private static IReadOnlyList<string> DeserializeList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(value, JsonOptions) ?? new List<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static TaskJournalParticipant ToParticipantEntity(TaskParticipantDraft draft)
        => new()
        {
            UserId = draft.UserId,
            Role = draft.Role,
            DisplayName = draft.DisplayName,
            Email = draft.Email,
            SortOrder = draft.SortOrder,
            ApprovalStatus = draft.ApprovalStatus,
            Resolution = draft.Resolution,
            RespondedAt = string.Equals(draft.ApprovalStatus, "pending", StringComparison.OrdinalIgnoreCase)
                ? null
                : DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

    private static TaskJournalTemplateParticipant ToTemplateParticipantEntity(TaskTemplateParticipantDraft draft)
        => new()
        {
            UserId = draft.UserId,
            Role = draft.Role,
            DisplayName = draft.DisplayName,
            Email = draft.Email,
            SortOrder = draft.SortOrder,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

    private static string? ValidateStatusRules(
        string status,
        string? result,
        IEnumerable<TaskParticipantDraft> participants,
        bool requireCompletionResult,
        bool allowPendingApproverCompletion = false)
    {
        var approvers = participants
            .Where(participant => string.Equals(participant.Role, "approver", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return ValidateStatusRules(
            status,
            result,
            approvers.Count,
            approvers.Count == 0 || approvers.All(participant =>
            string.Equals(participant.ApprovalStatus, "approved", StringComparison.OrdinalIgnoreCase)),
            requireCompletionResult,
            allowPendingApproverCompletion);
    }

    private static TaskParticipantsResolution ResetApprovalQueueIfStartingApproval(
        string? currentStatus,
        string nextStatus,
        TaskParticipantsResolution participants)
    {
        if (!ShouldRestartApprovalQueue(currentStatus, nextStatus))
        {
            return participants;
        }

        var resetItems = participants.Items
            .Select(participant => string.Equals(participant.Role, "approver", StringComparison.OrdinalIgnoreCase)
                                   && !string.Equals(participant.ApprovalStatus, "approved", StringComparison.OrdinalIgnoreCase)
                ? participant with { ApprovalStatus = "pending", Resolution = null }
                : participant)
            .ToList();

        return TaskParticipantsResolution.Valid(
            participants.PrimaryAssigneeUserId,
            participants.PrimaryAssigneeName,
            resetItems);
    }

    private static bool ResetApprovalQueueIfStartingApproval(
        string? currentStatus,
        string nextStatus,
        IEnumerable<TaskJournalParticipant> participants)
    {
        if (!ShouldRestartApprovalQueue(currentStatus, nextStatus))
        {
            return false;
        }

        var restarted = false;
        var now = DateTime.UtcNow;
        foreach (var participant in participants.Where(item => string.Equals(item.Role, "approver", StringComparison.OrdinalIgnoreCase)))
        {
            if (string.Equals(participant.ApprovalStatus, "approved", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.Equals(participant.ApprovalStatus, "pending", StringComparison.OrdinalIgnoreCase)
                || participant.Resolution is not null
                || participant.RespondedAt is not null)
            {
                restarted = true;
            }

            participant.ApprovalStatus = "pending";
            participant.Resolution = null;
            participant.RespondedAt = null;
            participant.UpdatedAt = now;
        }

        return restarted;
    }

    private static bool ShouldRestartApprovalQueue(string? currentStatus, string nextStatus)
        => string.Equals(nextStatus, "onApproval", StringComparison.OrdinalIgnoreCase)
           && !IsApprovalActiveStatus(currentStatus);

    private static bool IsApprovalActiveStatus(string? status)
        => string.Equals(status, "onApproval", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "overdueApproval", StringComparison.OrdinalIgnoreCase);

    private static string? ValidateStatusRules(
        string status,
        string? result,
        IEnumerable<TaskJournalParticipant> participants,
        bool requireCompletionResult,
        bool allowPendingApproverCompletion = false)
    {
        var approvers = participants
            .Where(participant => string.Equals(participant.Role, "approver", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return ValidateStatusRules(
            status,
            result,
            approvers.Count,
            approvers.Count == 0 || approvers.All(participant =>
                string.Equals(participant.ApprovalStatus, "approved", StringComparison.OrdinalIgnoreCase)),
            requireCompletionResult,
            allowPendingApproverCompletion);
    }

    private static string? ValidateStatusRules(
        string status,
        string? result,
        int approverCount,
        bool allApproversApproved,
        bool requireCompletionResult,
        bool allowPendingApproverCompletion = false)
    {
        if (string.Equals(status, "done", StringComparison.OrdinalIgnoreCase)
            && requireCompletionResult
            && string.IsNullOrWhiteSpace(result))
        {
            return "Перед завершением задачи заполните результат.";
        }

        if (IsApprovalActiveStatus(status)
            && requireCompletionResult
            && string.IsNullOrWhiteSpace(result))
        {
            return "Перед отправкой задачи на согласование заполните результат.";
        }

        if (string.Equals(status, "done", StringComparison.OrdinalIgnoreCase)
            && approverCount > 0
            && !allApproversApproved
            && !allowPendingApproverCompletion)
        {
            return "Нельзя завершить задачу, пока все согласующие не согласовали задачу.";
        }

        if (string.Equals(status, "onApproval", StringComparison.OrdinalIgnoreCase)
            && approverCount == 0)
        {
            return "Для отправки задачи на согласование добавьте хотя бы одного согласующего.";
        }

        if (string.Equals(status, "approved", StringComparison.OrdinalIgnoreCase)
            && approverCount == 0)
        {
            return "Для статуса \"Согласовано\" добавьте хотя бы одного согласующего.";
        }

        if (string.Equals(status, "approved", StringComparison.OrdinalIgnoreCase)
            && !allApproversApproved)
        {
            return "Статус \"Согласовано\" доступен только после согласования всеми согласующими.";
        }

        return null;
    }

    private static bool ParticipantMatches(TaskParticipantDraft participant, int? userId, string displayName)
    {
        if (participant.UserId.HasValue && userId.HasValue)
        {
            return participant.UserId.Value == userId.Value;
        }

        return string.Equals(participant.DisplayName, displayName, StringComparison.OrdinalIgnoreCase);
    }

    private static string ParticipantKey(TaskParticipantDraft participant)
        => participant.UserId.HasValue
            ? $"{participant.Role}:user:{participant.UserId.Value}"
            : $"{participant.Role}:name:{participant.DisplayName.Trim().ToLowerInvariant()}";

    private static string BuildUserDisplayName(string? fullName, string email)
        => string.IsNullOrWhiteSpace(fullName) ? email : fullName.Trim();

    private void AddTaskHistory(TaskJournalItem entity, TaskAccess access, string text, object? metadata = null)
    {
        var comment = CreateTaskComment(
            entity.Id,
            access,
            "system",
            text,
            metadata is null ? "{}" : JsonSerializer.Serialize(metadata, JsonOptions));

        if (entity.Id <= 0)
        {
            entity.Comments.Add(comment);
        }
        else
        {
            _db.TaskJournalComments.Add(comment);
        }
    }

    private async Task AppendTaskChatSystemMessagesAsync(
        IReadOnlyCollection<(int TaskId, IReadOnlyList<string> ChangeLabels)> changes,
        TaskAccess access,
        CancellationToken ct)
    {
        var relevant = changes
            .Select(item => (item.TaskId, Labels: item.ChangeLabels
                .Where(label => label.StartsWith("Статус", StringComparison.OrdinalIgnoreCase)
                    || label.StartsWith("Исполнитель", StringComparison.OrdinalIgnoreCase))
                .ToList()))
            .Where(item => item.TaskId > 0 && item.Labels.Count > 0)
            .ToList();
        if (relevant.Count == 0) return;

        var taskIds = relevant.Select(item => item.TaskId).Distinct().ToList();
        var threads = await _db.InternalChatThreads
            .Where(thread => thread.TaskJournalItemId.HasValue && taskIds.Contains(thread.TaskJournalItemId.Value))
            .ToDictionaryAsync(thread => thread.TaskJournalItemId!.Value, ct);
        if (threads.Count == 0) return;

        var now = DateTime.UtcNow;
        foreach (var item in relevant)
        {
            if (!threads.TryGetValue(item.TaskId, out var thread)) continue;
            _db.InternalChatMessages.Add(new InternalChatMessage
            {
                ThreadId = thread.Id,
                AuthorUserId = access.UserId,
                Kind = InternalChatMessageKinds.System,
                Text = $"{access.DisplayName}: {string.Join("; ", item.Labels)}",
                CreatedAt = now
            });
            thread.LastMessageAt = now;
            thread.UpdatedAt = now;
        }
        await _db.SaveChangesAsync(ct);
    }

    private async Task SynchronizeExistingTaskChatMembersAsync(
        IReadOnlyCollection<TaskJournalItem> tasks,
        CancellationToken ct)
    {
        if (tasks.Count == 0) return;
        var taskIds = tasks.Select(task => task.Id).Where(id => id > 0).Distinct().ToList();
        var threads = await _db.InternalChatThreads
            .Include(thread => thread.Members)
            .Where(thread => thread.TaskJournalItemId.HasValue && taskIds.Contains(thread.TaskJournalItemId.Value))
            .ToListAsync(ct);
        if (threads.Count == 0) return;

        var tasksById = tasks.ToDictionary(task => task.Id);
        var now = DateTime.UtcNow;
        foreach (var thread in threads)
        {
            if (!thread.TaskJournalItemId.HasValue || !tasksById.TryGetValue(thread.TaskJournalItemId.Value, out var task)) continue;
            var desiredIds = task.Participants
                .Where(participant => participant.UserId.HasValue)
                .Select(participant => participant.UserId!.Value)
                .ToHashSet();
            if (task.AuthorUserId.HasValue) desiredIds.Add(task.AuthorUserId.Value);
            if (task.AssigneeUserId.HasValue) desiredIds.Add(task.AssigneeUserId.Value);
            foreach (var member in thread.Members)
            {
                member.IsActive = desiredIds.Contains(member.UserId)
                    || member.Role == "guest" && member.IsActive;
                if (desiredIds.Contains(member.UserId))
                    member.Role = member.UserId == task.AuthorUserId ? "owner" : "member";
                member.UpdatedAt = now;
            }
            foreach (var userId in desiredIds.Where(id => thread.Members.All(member => member.UserId != id)))
                thread.Members.Add(new InternalChatMember
                {
                    UserId = userId,
                    Role = userId == task.AuthorUserId ? "owner" : "member",
                    JoinedAt = now
                });
            thread.Name = task.Title;
            thread.UpdatedAt = now;
        }
        await _db.SaveChangesAsync(ct);
    }

    private static TaskJournalComment CreateTaskComment(
        int taskId,
        TaskAccess access,
        string kind,
        string text,
        string metadataJson = "{}")
        => new()
        {
            TaskJournalItemId = taskId,
            AuthorUserId = access.UserId,
            AuthorName = access.DisplayName,
            Kind = kind,
            Text = text,
            MetadataJson = metadataJson,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

    private static TaskJournalCommentDto MapComment(TaskJournalComment comment)
        => new()
        {
            Id = comment.Id,
            TaskJournalItemId = comment.TaskJournalItemId,
            AuthorUserId = comment.AuthorUserId,
            AuthorName = comment.AuthorName,
            Kind = comment.Kind,
            Text = comment.Text,
            IsExternalCalendarVisible = comment.IsExternalCalendarVisible,
            ExternalSource = comment.ExternalSource,
            ExternalAuthorTrust = comment.ExternalAuthorTrust,
            MetadataJson = string.IsNullOrWhiteSpace(comment.MetadataJson) ? "{}" : comment.MetadataJson,
            CreatedAt = ToIso(comment.CreatedAt),
            UpdatedAt = comment.UpdatedAt.HasValue ? ToIso(comment.UpdatedAt.Value) : null,
        };

    private static TaskJournalDeadlineChangeRequestDto MapDeadlineChangeRequest(TaskJournalDeadlineChangeRequest request)
        => new()
        {
            Id = request.Id,
            TaskJournalItemId = request.TaskJournalItemId,
            RequestedByUserId = request.RequestedByUserId,
            RequestedByName = request.RequestedByName,
            ReviewerUserId = request.ReviewerUserId,
            ReviewerName = request.ReviewerName,
            CurrentDueAt = ToIso(request.CurrentDueAt),
            RequestedDueAt = ToIso(request.RequestedDueAt),
            Reason = request.Reason,
            Status = request.Status,
            DecisionDueAt = request.DecisionDueAt.HasValue ? ToIso(request.DecisionDueAt.Value) : null,
            DecisionComment = request.DecisionComment ?? string.Empty,
            DecidedByUserId = request.DecidedByUserId,
            DecidedByName = request.DecidedByName ?? string.Empty,
            DecidedAt = request.DecidedAt.HasValue ? ToIso(request.DecidedAt.Value) : null,
            CreatedCommentId = request.CreatedCommentId,
            DecisionCommentId = request.DecisionCommentId,
            CreatedAt = ToIso(request.CreatedAt),
            UpdatedAt = request.UpdatedAt.HasValue ? ToIso(request.UpdatedAt.Value) : null,
        };

    private async Task<Dictionary<string, int>> ResolveTaskCaseDocumentIdsAsync(
        int? clientCaseId,
        IReadOnlyCollection<TaskJournalFile> files,
        CancellationToken ct)
    {
        if (!clientCaseId.HasValue || files.Count == 0)
        {
            return [];
        }

        var caseDocumentKeys = files
            .Select(file => BuildTaskCaseDocumentKey(file.TaskJournalItemId, file.DocumentKey))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return await _db.ClientCaseDocuments
            .AsNoTracking()
            .Where(item => item.ClientCaseId == clientCaseId.Value && caseDocumentKeys.Contains(item.DocumentKey))
            .Select(item => new
            {
                item.Id,
                item.DocumentKey,
                item.Version,
            })
            .ToDictionaryAsync(
                item => BuildTaskCaseDocumentLookupKey(item.DocumentKey, item.Version),
                item => item.Id,
                StringComparer.OrdinalIgnoreCase,
                ct);
    }

    private static IReadOnlyList<TaskJournalFileDto> MapFiles(
        IReadOnlyCollection<TaskJournalFile> files,
        IReadOnlyDictionary<string, int>? caseDocumentIds = null)
    {
        var latestVersions = files
            .GroupBy(file => file.DocumentKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Max(file => file.Version),
                StringComparer.OrdinalIgnoreCase);

        return files
            .Select(file => MapFile(
                file,
                latestVersions.TryGetValue(file.DocumentKey, out var latest) && file.Version == latest,
                caseDocumentIds?.GetValueOrDefault(BuildTaskCaseDocumentLookupKey(file.DocumentKey, file.Version))))
            .ToList();
    }

    private static TaskJournalFileDto MapFile(TaskJournalFile file, bool isLatestVersion, int? clientCaseDocumentId = null)
        => new()
        {
            Id = file.Id,
            TaskJournalItemId = file.TaskJournalItemId,
            DocumentKey = file.DocumentKey,
            Version = file.Version,
            OriginalFileName = file.OriginalFileName,
            ContentType = file.ContentType,
            SizeBytes = file.SizeBytes,
            UploadedByUserId = file.UploadedByUserId,
            UploadedByName = file.UploadedByName,
            UploadedAt = ToIso(file.UploadedAt),
            IsLatestVersion = isLatestVersion,
            ClientCaseDocumentId = clientCaseDocumentId,
        };

    private static string? NormalizeDocumentKey(string? value)
    {
        var normalized = NormalizeOptional(value);
        if (normalized is null)
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

    private static string SanitizeFileName(string? value)
    {
        var rawValue = value ?? string.Empty;
        try
        {
            rawValue = Uri.UnescapeDataString(rawValue);
        }
        catch (UriFormatException)
        {
            // Keep the original name if it is not a valid URI-escaped string.
        }

        var fileName = rawValue
            .Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault()
            ?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "file";
        }

        fileName = fileName.Replace("..", "_", StringComparison.Ordinal);

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

    private static string BuildTaskCaseDocumentKey(int taskId, string taskDocumentKey)
    {
        var prefix = $"task-{taskId}-";
        var maxSuffixLength = Math.Max(1, 64 - prefix.Length);
        var suffix = taskDocumentKey.Length <= maxSuffixLength
            ? taskDocumentKey
            : taskDocumentKey[..maxSuffixLength];

        return prefix + suffix;
    }

    private static string BuildTaskCaseDocumentLookupKey(string documentKey, int version)
        => $"{documentKey}:{version.ToString(CultureInfo.InvariantCulture)}";

    private string ResolveTaskFilePath(string storagePath)
    {
        var root = Path.GetFullPath(_taskFilesRoot);
        var fullPath = Path.GetFullPath(Path.Combine(root, storagePath));
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Некорректный путь файла задачи.");
        }

        return fullPath;
    }

    private bool IsTaskFileAvailable(TaskJournalFile file)
    {
        try
        {
            return System.IO.File.Exists(ResolveTaskFilePath(file.StoragePath));
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
            or ArgumentException
            or NotSupportedException)
        {
            return false;
        }
    }

    private static void TryDeletePhysicalFile(string fullPath)
    {
        try
        {
            if (System.IO.File.Exists(fullPath))
            {
                System.IO.File.Delete(fullPath);
            }
        }
        catch
        {
            // Файл уже удален или временно занят. Запись в БД важнее физической уборки.
        }
    }

    private static bool IsTaskAuthor(TaskJournalItem entity, int userId)
        => entity.AuthorUserId == userId;

    private static bool IsTaskExecutor(TaskJournalItem entity, int userId)
        => entity.AssigneeUserId == userId ||
           entity.Participants.Any(participant =>
               participant.UserId == userId &&
               string.Equals(participant.Role, "assignee", StringComparison.OrdinalIgnoreCase));

    private static bool IsTaskOwnedByUser(TaskJournalItem entity, int userId)
        => IsTaskAuthor(entity, userId) || IsTaskExecutor(entity, userId);

    private static bool ShouldRestrictExecutorFullEdit(
        TaskJournalItem entity,
        TaskAccess access,
        TaskJournalSettingsDto settings)
        => settings.RestrictExecutorEditing &&
           !access.IsAdmin &&
           IsTaskExecutor(entity, access.UserId) &&
           !IsTaskAuthor(entity, access.UserId);

    private static bool CanFullyModifyTask(
        TaskJournalItem entity,
        TaskAccess access,
        bool hasOwnCapability,
        bool hasOtherCapability,
        TaskJournalSettingsDto settings)
    {
        if (access.IsAdmin)
        {
            return true;
        }

        var isOwnTask = IsTaskOwnedByUser(entity, access.UserId);
        var hasCapability = isOwnTask ? hasOwnCapability : hasOtherCapability;
        if (!hasCapability || (isOwnTask && ShouldRestrictExecutorFullEdit(entity, access, settings)))
        {
            return false;
        }

        return true;
    }

    private static bool CanCompleteTask(
        TaskJournalItem entity,
        TaskAccess access,
        TaskJournalSettingsDto settings)
    {
        if (access.IsAdmin)
        {
            return true;
        }

        if (!IsTaskOwnedByUser(entity, access.UserId))
        {
            return access.Permission?.CanCompleteOthers == true;
        }

        if (CanExecutorResubmitReturnedTask(entity, access.UserId))
        {
            return true;
        }

        if (CanFullyModifyTask(
                entity,
                access,
                access.Permission?.CanEdit == true,
                access.Permission?.CanEditOthers == true,
                settings))
        {
            return true;
        }

        return IsTaskExecutor(entity, access.UserId)
            && settings.ExecutorCompletionAllowedUserIds.Contains(access.UserId);
    }

    private static bool CanDeleteTask(
        TaskJournalItem entity,
        TaskAccess access,
        TaskJournalSettingsDto settings)
        => CanFullyModifyTask(
            entity,
            access,
            access.Permission?.CanDelete == true,
            access.Permission?.CanDeleteOthers == true,
            settings);

    private static bool IsCompletionStatus(string? status)
        => string.Equals(status, "done", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "onApproval", StringComparison.OrdinalIgnoreCase);

    private static bool CanExecutorSubmitTaskForApproval(TaskJournalItem entity, int userId)
        => IsTaskExecutor(entity, userId)
           && !string.Equals(entity.Status, "done", StringComparison.OrdinalIgnoreCase)
           && !string.Equals(entity.Status, "cancelled", StringComparison.OrdinalIgnoreCase)
           && !IsApprovalActiveStatus(entity.Status);

    private async Task<(TaskParticipantsResolution Participants, bool Added)> AddAuthorAsApproverIfMissingAsync(
        TaskParticipantsResolution participants,
        int? authorUserId,
        string nextStatus,
        CancellationToken ct)
    {
        var authorApprover = await ResolveAuthorApproverIfMissingAsync(
            authorUserId,
            nextStatus,
            participants.Items.Any(participant =>
                string.Equals(participant.Role, "approver", StringComparison.OrdinalIgnoreCase)),
            ct);
        if (authorApprover is null)
        {
            return (participants, false);
        }

        var items = participants.Items.ToList();
        items.Add(authorApprover with
        {
            SortOrder = items.Count == 0
                ? 0
                : items.Max(participant => participant.SortOrder) + 1,
        });
        return (TaskParticipantsResolution.Valid(
            participants.PrimaryAssigneeUserId,
            participants.PrimaryAssigneeName,
            items), true);
    }

    private async Task<bool> AddAuthorAsApproverIfMissingAsync(
        TaskJournalItem entity,
        string nextStatus,
        CancellationToken ct)
    {
        var authorApprover = await ResolveAuthorApproverIfMissingAsync(
            entity.AuthorUserId,
            nextStatus,
            entity.Participants.Any(participant =>
                string.Equals(participant.Role, "approver", StringComparison.OrdinalIgnoreCase)),
            ct);
        if (authorApprover is null)
        {
            return false;
        }

        entity.Participants.Add(new TaskJournalParticipant
        {
            UserId = authorApprover.UserId,
            Role = authorApprover.Role,
            DisplayName = authorApprover.DisplayName,
            Email = authorApprover.Email,
            SortOrder = entity.Participants.Count == 0
                ? 0
                : entity.Participants.Max(participant => participant.SortOrder) + 1,
            ApprovalStatus = authorApprover.ApprovalStatus,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        return true;
    }

    private async Task<TaskParticipantDraft?> ResolveAuthorApproverIfMissingAsync(
        int? authorUserId,
        string nextStatus,
        bool hasApprover,
        CancellationToken ct)
    {
        if (!string.Equals(nextStatus, "onApproval", StringComparison.OrdinalIgnoreCase)
            || hasApprover
            || !authorUserId.HasValue)
        {
            return null;
        }

        var author = await _db.Users
            .AsNoTracking()
            .Where(user => user.Id == authorUserId.Value && user.IsActive && user.IsApproved)
            .Select(user => new
            {
                user.Id,
                user.FullName,
                user.Email,
            })
            .SingleOrDefaultAsync(ct);
        if (author is null)
        {
            return null;
        }

        return new TaskParticipantDraft(
            author.Id,
            string.IsNullOrWhiteSpace(author.FullName) ? author.Email : author.FullName.Trim(),
            author.Email,
            "approver",
            0,
            "pending",
            null);
    }

    private static bool CanUpdateTaskStatus(
        TaskJournalItem entity,
        TaskAccess access,
        TaskJournalSettingsDto settings,
        string nextStatus)
    {
        if (access.IsAdmin)
        {
            return true;
        }

        var isCompletionAction = IsCompletionStatus(nextStatus);
        if (!IsTaskOwnedByUser(entity, access.UserId))
        {
            return isCompletionAction
                ? access.Permission?.CanCompleteOthers == true
                : access.Permission?.CanEditOthers == true;
        }

        if (string.Equals(nextStatus, "onApproval", StringComparison.OrdinalIgnoreCase)
            && CanExecutorSubmitTaskForApproval(entity, access.UserId))
        {
            return true;
        }

        if (CanFullyModifyTask(
                entity,
                access,
                access.Permission?.CanEdit == true,
                access.Permission?.CanEditOthers == true,
                settings))
        {
            return true;
        }

        if (isCompletionAction && IsTaskExecutor(entity, access.UserId) && !IsTaskAuthor(entity, access.UserId))
        {
            return settings.ExecutorCompletionAllowedUserIds.Contains(access.UserId);
        }

        return CanPostTaskComment(entity, access);
    }

    private static bool CanUploadTaskFile(
        TaskJournalItem entity,
        TaskAccess access,
        TaskJournalSettingsDto settings)
    {
        if (!ShouldRestrictExecutorFullEdit(entity, access, settings))
        {
            return true;
        }

        return IsTaskExecutor(entity, access.UserId);
    }

    /// <summary>
    /// Возврат согласующим создаёт обязательный обратный путь: назначенный исполнитель
    /// может повторно сдать исправленный результат, даже если ему запрещено самостоятельно
    /// завершать остальные задачи. Одобренные ранее шаги очереди при этом сохраняются.
    /// </summary>
    private static bool CanExecutorResubmitReturnedTask(TaskJournalItem entity, int userId)
        => IsTaskExecutor(entity, userId)
           && (string.Equals(entity.Status, "revision", StringComparison.OrdinalIgnoreCase)
               || string.Equals(entity.Status, "inProgress", StringComparison.OrdinalIgnoreCase)
               || string.Equals(entity.Status, "waiting", StringComparison.OrdinalIgnoreCase))
           && entity.Participants.Any(participant =>
               string.Equals(participant.Role, "approver", StringComparison.OrdinalIgnoreCase)
               && string.Equals(participant.ApprovalStatus, "revisionRequested", StringComparison.OrdinalIgnoreCase));

    private static bool CanAdminForceCompleteTask(
        TaskAccess access,
        TaskJournalSettingsDto settings,
        string? nextStatus)
        => access.IsAdmin
           && settings.AllowAdminCompleteAnyTask
           && string.Equals(nextStatus, "done", StringComparison.OrdinalIgnoreCase);

    private static bool CanPostTaskComment(TaskJournalItem entity, TaskAccess access)
    {
        if (access.IsAdmin
            || (access.Permission?.CanEdit == true
                && (access.Visibility.SeesAll
                    || string.Equals(access.Visibility.ViewScope, DataVisibilityScope.Department, StringComparison.OrdinalIgnoreCase))))
        {
            return true;
        }

        if (entity.AuthorUserId == access.UserId || entity.AssigneeUserId == access.UserId)
        {
            return true;
        }

        return entity.Participants.Any(participant =>
            participant.UserId == access.UserId &&
            !string.Equals(participant.Role, "observer", StringComparison.OrdinalIgnoreCase));
    }

    private bool ReplacePrimaryAssignee(TaskJournalItem entity, User assignee, DateTime nowUtc)
    {
        var displayName = BuildUserDisplayName(assignee.FullName, assignee.Email);
        var currentAssignees = entity.Participants
            .Where(participant => string.Equals(participant.Role, "assignee", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var alreadySame =
            entity.AssigneeUserId == assignee.Id &&
            string.Equals(entity.AssigneeName, displayName, StringComparison.Ordinal) &&
            currentAssignees.Count == 1 &&
            currentAssignees[0].UserId == assignee.Id &&
            string.Equals(currentAssignees[0].DisplayName, displayName, StringComparison.Ordinal);
        if (alreadySame)
        {
            return false;
        }

        if (currentAssignees.Count > 0)
        {
            _db.TaskJournalParticipants.RemoveRange(currentAssignees);
            foreach (var participant in currentAssignees)
            {
                entity.Participants.Remove(participant);
            }
        }

        entity.AssigneeUserId = assignee.Id;
        entity.AssigneeName = displayName;
        entity.Participants.Add(new TaskJournalParticipant
        {
            UserId = assignee.Id,
            Role = "assignee",
            DisplayName = displayName,
            Email = assignee.Email,
            SortOrder = 0,
            ApprovalStatus = "pending",
            CreatedAt = nowUtc,
            UpdatedAt = nowUtc,
        });

        return true;
    }

    private static bool ApplyTaskControlState(TaskJournalItem entity, bool isUnderControl, TaskAccess access)
    {
        if (entity.IsUnderControl == isUnderControl)
        {
            return false;
        }

        entity.IsUnderControl = isUnderControl;
        if (isUnderControl)
        {
            entity.ControlMarkedAt = DateTime.UtcNow;
            entity.ControlMarkedByUserId = access.UserId;
            entity.ControlMarkedByUserName = access.DisplayName;
        }
        else
        {
            entity.ControlMarkedAt = null;
            entity.ControlMarkedByUserId = null;
            entity.ControlMarkedByUserName = string.Empty;
        }

        return true;
    }

    private async Task<bool> EnsureTaskObserverAsync(TaskJournalItem entity, int userId, CancellationToken ct)
    {
        if (entity.Participants.Any(participant => participant.UserId == userId))
        {
            return false;
        }

        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(item => item.Id == userId, ct);
        if (user is null)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        entity.Participants.Add(new TaskJournalParticipant
        {
            UserId = user.Id,
            Role = "observer",
            DisplayName = BuildUserDisplayName(user.FullName, user.Email),
            Email = user.Email,
            SortOrder = entity.Participants.Count,
            ApprovalStatus = "pending",
            CreatedAt = now,
            UpdatedAt = now,
        });

        return true;
    }

    private static IReadOnlyList<string> BuildTaskChangeLabels(
        TaskJournalItem entity,
        UpsertTaskJournalItemRequest request,
        TaskClientBinding binding,
        TaskParticipantsResolution participants,
        string nextStatus)
    {
        var changes = new List<string>();

        var nextObjectName = BuildObjectName(binding.OwnerName, binding.ClientCaseTitle);
        AddIfChanged(changes, "Клиент/дело", entity.ObjectName, nextObjectName);
        AddIfChanged(changes, "Наименование", entity.Title, request.Title.Trim());
        AddIfChanged(changes, "Описание", NormalizeOptional(entity.Description), NormalizeOptional(request.Description));
        AddIfChanged(changes, "Исполнитель", entity.AssigneeName, participants.PrimaryAssigneeName);
        if (entity.IsUnderControl != request.IsUnderControl)
        {
            changes.Add(request.IsUnderControl ? "контроль: включен" : "контроль: выключен");
        }
        var nextDueAt = NormalizeUtcInstant(request.DueAt);
        if (entity.DueAt != nextDueAt)
        {
            changes.Add($"Срок: {FormatHistoryDateTime(entity.DueAt)} -> {FormatHistoryDateTime(nextDueAt)}");
        }
        var nextApprovalDueAt = NormalizeApprovalDueAt(request.ApprovalDueAt);
        if (entity.ApprovalDueAt != nextApprovalDueAt)
        {
            changes.Add($"Срок согласования: {FormatOptionalDateTime(entity.ApprovalDueAt)} -> {FormatOptionalDateTime(nextApprovalDueAt)}");
        }
        var nextDuration = NormalizeTaskDuration(request.PlannedDurationMinutes);
        if (entity.PlannedDurationMinutes != nextDuration)
        {
            changes.Add($"Длительность: {FormatDurationMinutes(entity.PlannedDurationMinutes)} -> {FormatDurationMinutes(nextDuration)}");
        }

        AddIfChanged(changes, "Тип", TaskTypeLabel(entity.Type), TaskTypeLabel(NormalizeEnumValue(request.Type, AllowedTypes, "task")));
        AddIfChanged(changes, "Приоритет", TaskPriorityLabel(entity.Priority), TaskPriorityLabel(NormalizeEnumValue(request.Priority, AllowedPriorities, "normal")));
        AddIfChanged(changes, "Статус", TaskStatusLabel(entity.Status), TaskStatusLabel(nextStatus));
        AddIfChanged(changes, "Результат", NormalizeOptional(entity.Result), NormalizeOptional(request.Result));
        var currentReminders = DeserializeList(entity.RemindersJson);
        var nextReminders = SerializeListForCompare(request.Reminders, AllowedReminders, ["atDue"]);
        if (!SameStringSet(currentReminders, nextReminders))
        {
            changes.Add($"Напоминания: {FormatReminderList(currentReminders)} -> {FormatReminderList(nextReminders)}");
        }

        var currentChannels = DeserializeList(entity.ChannelsJson);
        var nextChannels = SerializeListForCompare(request.Channels, AllowedChannels, ["inApp"]);
        if (!SameStringSet(currentChannels, nextChannels))
        {
            changes.Add($"Каналы уведомлений: {FormatChannelList(currentChannels)} -> {FormatChannelList(nextChannels)}");
        }

        var currentTags = DeserializeList(entity.TagsJson);
        var nextTags = SerializeFreeListForCompare(request.Tags);
        if (!SameStringSet(currentTags, nextTags))
        {
            changes.Add($"Теги: {FormatStringList(currentTags)} -> {FormatStringList(nextTags)}");
        }

        if (!SameParticipants(entity.Participants, participants.Items))
        {
            changes.Add("Участники: список обновлен");
        }

        AddIfChanged(changes, "Оплата исполнителю", FormatYesNo(entity.ContractorPaymentEnabled), FormatYesNo(request.ContractorPaymentEnabled));
        AddIfChanged(changes, "Сумма оплаты исполнителю", FormatMoney(NormalizeMoneyAmount(entity.ContractorPaymentAmount)), FormatMoney(NormalizeMoneyAmount(request.ContractorPaymentAmount)));
        AddIfChanged(changes, "Дата оплаты исполнителю", FormatOptionalDate(NormalizeDateOnly(entity.ContractorPaymentDueDate)), FormatOptionalDate(NormalizeDateOnly(request.ContractorPaymentDueDate)));
        AddIfChanged(changes, "Оплата по результату", FormatYesNo(entity.ContractorPaymentOnResult), FormatYesNo(request.ContractorPaymentOnResult));
        AddIfChanged(changes, "Срок оплаты по результату", FormatOptionalNumber(entity.ContractorPaymentResultDelayDays), FormatOptionalNumber(NormalizeContractorPaymentResultDelayDays(request.ContractorPaymentResultDelayDays)));

        var currentRecurrence = FormatRecurrence(entity);
        var nextRecurrence = FormatRecurrence(request);
        AddIfChanged(changes, "Повторение", currentRecurrence, nextRecurrence);

        return changes;
    }

    private static string FormatRecurrence(TaskJournalItem entity)
        => entity.RecurrenceEnabled
            ? FormatRecurrence(
                entity.RecurrenceFrequency,
                entity.RecurrenceInterval,
                entity.RecurrenceDayOfWeek,
                entity.RecurrenceDayOfMonth,
                entity.RecurrenceMonth,
                entity.RecurrenceTimeMinutes,
                entity.RecurrenceSkipWeekends,
                entity.RecurrenceTimeZoneId)
            : "выключено";

    private static string FormatRecurrence(UpsertTaskJournalItemRequest request)
        => request.RecurrenceEnabled
            ? FormatRecurrence(
                request.RecurrenceFrequency,
                request.RecurrenceInterval ?? 1,
                request.RecurrenceDayOfWeek,
                request.RecurrenceDayOfMonth,
                request.RecurrenceMonth,
                request.RecurrenceTimeMinutes ?? 9 * 60,
                request.RecurrenceSkipWeekends,
                request.RecurrenceTimeZoneId)
            : "выключено";

    private static string FormatRecurrence(
        string? frequency,
        int interval,
        int? dayOfWeek,
        int? dayOfMonth,
        int? month,
        int? timeMinutes,
        bool skipWeekends,
        string? timeZoneId)
    {
        var normalizedFrequency = frequency?.Trim().ToLowerInvariant();
        var frequencyLabel = normalizedFrequency switch
        {
            "daily" => "ежедневно",
            "weekly" => "еженедельно",
            "monthly" => "ежемесячно",
            "yearly" => "ежегодно",
            _ => "по расписанию",
        };
        var details = new List<string> { frequencyLabel };
        if (interval > 1)
        {
            details.Add($"интервал {interval}");
        }
        if (normalizedFrequency == "weekly" && dayOfWeek.HasValue)
        {
            var weekDays = new[] { "воскресенье", "понедельник", "вторник", "среда", "четверг", "пятница", "суббота" };
            details.Add(weekDays[Math.Clamp(dayOfWeek.Value, 0, 6)]);
        }
        if ((normalizedFrequency == "monthly" || normalizedFrequency == "yearly") && dayOfMonth.HasValue)
        {
            details.Add($"число {dayOfMonth.Value}");
        }
        if (normalizedFrequency == "yearly" && month.HasValue)
        {
            details.Add($"месяц {month.Value}");
        }
        if (timeMinutes.HasValue)
        {
            details.Add($"время {timeMinutes.Value / 60:00}:{timeMinutes.Value % 60:00}");
        }
        if (skipWeekends)
        {
            details.Add("выходные переносятся на понедельник");
        }
        details.Add($"часовой пояс {timeZoneId?.Trim() ?? "UTC"}");
        return string.Join(", ", details);
    }

    private static void AddIfChanged<T>(ICollection<string> changes, string label, T current, T next)
    {
        if (!EqualityComparer<T>.Default.Equals(current, next))
        {
            changes.Add($"{label}: {FormatChangeValue(current)} -> {FormatChangeValue(next)}");
        }
    }

    private static bool HasTaskAssigneeChange(IEnumerable<string> changes)
        => changes.Any(change =>
        {
            var normalized = change.Trim();
            return normalized.Equals("Исполнитель", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("Исполнитель:", StringComparison.OrdinalIgnoreCase);
        });

    private static string FormatChangeValue<T>(T value)
    {
        if (value is null)
        {
            return "не указано";
        }

        var text = value.ToString();
        return string.IsNullOrWhiteSpace(text) ? "не указано" : text.Trim();
    }

    private static string FormatOptionalDateTime(DateTime? value)
        => value.HasValue ? FormatHistoryDateTime(value.Value) : "не указано";

    private static string FormatOptionalDate(DateTime? value)
        => value.HasValue ? value.Value.ToString("dd.MM.yyyy") : "не указано";

    private static string FormatDurationMinutes(int minutes)
        => minutes <= 0 ? "не указано" : $"{minutes} мин.";

    private static string FormatMoney(decimal? value)
        => value.HasValue ? value.Value.ToString("N2", CultureInfo.GetCultureInfo("ru-RU")) + " ₽" : "не указано";

    private static string FormatOptionalNumber(int? value)
        => value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "не указано";

    private static string FormatYesNo(bool value)
        => value ? "да" : "нет";

    private static string FormatStringList(IReadOnlyList<string> values)
        => values.Count == 0 ? "не указано" : string.Join(", ", values);

    private static string FormatReminderList(IReadOnlyList<string> values)
        => values.Count == 0 ? "не указано" : string.Join(", ", values.Select(TaskReminderLabel));

    private static string FormatChannelList(IReadOnlyList<string> values)
        => values.Count == 0 ? "не указано" : string.Join(", ", values.Select(TaskChannelLabel));

    private static IReadOnlyList<string> SerializeListForCompare(
        IEnumerable<string>? values,
        HashSet<string> allowedValues,
        IReadOnlyCollection<string> fallback)
        => DeserializeList(SerializeList(values, allowedValues, fallback));

    private static IReadOnlyList<string> SerializeFreeListForCompare(IEnumerable<string>? values)
        => DeserializeList(SerializeFreeList(values));

    private static bool SameStringSet(IReadOnlyList<string> current, IReadOnlyList<string> next)
        => current
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(next.OrderBy(item => item, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

    private static bool SameParticipants(IEnumerable<TaskJournalParticipant> current, IEnumerable<TaskParticipantDraft> next)
    {
        var currentKeys = current
            .Select(item => $"{item.Role}:{item.UserId}:{item.DisplayName}:{item.SortOrder}:{item.ApprovalStatus}")
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var nextKeys = next
            .Select(item => $"{item.Role}:{item.UserId}:{item.DisplayName}:{item.SortOrder}:{item.ApprovalStatus}")
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return currentKeys.SequenceEqual(nextKeys, StringComparer.OrdinalIgnoreCase);
    }

    private static string BuildApprovalHistoryText(string decision, string approverName, string? resolution, string? taskResult)
    {
        var text = string.Equals(decision, "approved", StringComparison.OrdinalIgnoreCase)
            ? $"Согласовано: {approverName}."
            : $"Возвращено на доработку: {approverName}.";

        if (!string.IsNullOrWhiteSpace(taskResult))
        {
            text = $"{text} Результат исполнителя: {taskResult.Trim()}";
        }

        return string.IsNullOrWhiteSpace(resolution)
            ? text
            : string.Equals(decision, "revisionRequested", StringComparison.OrdinalIgnoreCase)
                ? $"{text} Рецензия: {resolution}"
                : $"{text} Резолюция: {resolution}";
    }

    private static string BuildDeadlineChangeRequestText(
        TaskJournalItem entity,
        string executorName,
        DateTime requestedDueAt,
        string? reason)
    {
        var text = $"{executorName} запросил перенос срока задачи с {FormatHistoryDateTime(entity.DueAt)} на {FormatHistoryDateTime(requestedDueAt)}.";
        return string.IsNullOrWhiteSpace(reason)
            ? text
            : $"{text} Причина: {reason}";
    }

    private static string BuildDeadlineChangeApprovalText(
        string reviewerName,
        DateTime previousDueAt,
        DateTime approvedDueAt,
        string? decisionComment)
    {
        var text = $"{reviewerName} согласовал перенос срока задачи с {FormatHistoryDateTime(previousDueAt)} на {FormatHistoryDateTime(approvedDueAt)}.";
        return string.IsNullOrWhiteSpace(decisionComment)
            ? text
            : $"{text} Комментарий: {decisionComment}";
    }

    private static string BuildDeadlineChangeRejectionText(
        string reviewerName,
        DateTime requestedDueAt,
        string decisionComment)
        => $"{reviewerName} отказал в переносе срока задачи на {FormatHistoryDateTime(requestedDueAt)}. Причина: {decisionComment}";

    private static string FormatHistoryDateTime(DateTime value)
        => DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("dd.MM.yyyy HH:mm");

    private static string ClientCaseStatusLabel(ClientCaseStatus status)
        => status switch
        {
            ClientCaseStatus.Open => "Открыто",
            ClientCaseStatus.OnHold => "Приостановлено",
            ClientCaseStatus.Closed => "Закрыто",
            ClientCaseStatus.Lost => "Проиграно",
            _ => status.ToString(),
        };

    private static string TaskStatusLabel(string? status)
        => status switch
        {
            "new" => "Новая",
            "inProgress" => "В работе",
            "waiting" => "Ожидает",
            "onApproval" => "На согласовании",
            "approved" => "Согласовано",
            "revision" => "На доработке",
            "overdueApproval" => "Просрочено на согласовании",
            "done" => "Готово",
            "cancelled" => "Отменена",
            _ => status ?? "—",
        };

    private static string TaskTypeLabel(string? type)
        => type switch
        {
            "task" => "Задача",
            "budget" => "Бюджет",
            "contact" => "Связаться",
            "document" => "Документ",
            "reminder" => "Не забыть",
            "control" => "Контроль",
            "courtHearing" => "Суд. заседание",
            "meeting" => "Встреча",
            _ => type ?? "—",
        };

    private static string TaskPriorityLabel(string? priority)
        => priority switch
        {
            "low" => "Низкий",
            "normal" => "Обычный",
            "high" => "Высокий",
            "urgent" => "Срочно",
            _ => priority ?? "—",
        };

    private static string TaskReminderLabel(string? reminder)
        => reminder switch
        {
            "before15m" => "за 15 минут",
            "before1h" => "за 1 час",
            "before3h" => "за 3 часа",
            "before1d" => "за 1 день",
            "before3d" => "за 3 дня",
            "before7d" => "за 7 дней",
            "atDue" => "в срок задачи",
            "dailyOverdue" => "ежедневно после просрочки",
            _ => reminder ?? "—",
        };

    private static string TaskChannelLabel(string? channel)
        => channel switch
        {
            "inApp" => "в системе",
            "email" => "Email",
            "telegram" => "Telegram",
            _ => channel ?? "—",
        };

    private static int ParticipantRoleOrder(TaskJournalParticipant participant)
        => ParticipantRoleOrder(participant.Role);

    private static int ParticipantRoleOrder(string role)
        => role switch
        {
            "assignee" => 0,
            "approver" => 1,
            "observer" => 2,
            _ => 3,
        };

    private sealed record TaskAccess(
        int UserId,
        string DisplayName,
        bool IsAdmin,
        RolePermission? Permission,
        DataVisibilityAccess Visibility);

    private sealed record TaskParticipantDraft(
        int? UserId,
        string DisplayName,
        string Email,
        string Role,
        int SortOrder,
        string ApprovalStatus,
        string? Resolution);

    private sealed record TaskParticipantsResolution(
        int? PrimaryAssigneeUserId,
        string PrimaryAssigneeName,
        IReadOnlyList<TaskParticipantDraft> Items,
        string? Error)
    {
        public static TaskParticipantsResolution Valid(int? primaryAssigneeUserId, string primaryAssigneeName, IReadOnlyList<TaskParticipantDraft> items)
            => new(primaryAssigneeUserId, primaryAssigneeName, items, null);

        public static TaskParticipantsResolution Invalid(string error)
            => new(null, string.Empty, Array.Empty<TaskParticipantDraft>(), error);
    }

    private sealed record TaskTemplateParticipantDraft(
        int? UserId,
        string DisplayName,
        string Email,
        string Role,
        int SortOrder);

    private sealed record TaskTemplateParticipantsResolution(
        IReadOnlyList<TaskTemplateParticipantDraft> Items,
        string? Error)
    {
        public static TaskTemplateParticipantsResolution Valid(IReadOnlyList<TaskTemplateParticipantDraft> items)
            => new(items, null);

        public static TaskTemplateParticipantsResolution Invalid(string error)
            => new(Array.Empty<TaskTemplateParticipantDraft>(), error);
    }

    private sealed record TaskClientBinding(
        int? ClientId,
        string ClientName,
        int? LegalEntityId,
        string LegalEntityName,
        int? ClientCaseId,
        string? ClientCaseTitle,
        string? Error)
    {
        public string OwnerName => !string.IsNullOrWhiteSpace(ClientName) ? ClientName : LegalEntityName;

        public static TaskClientBinding Valid(
            int? clientId,
            string clientName,
            int? legalEntityId,
            string legalEntityName,
            int? clientCaseId,
            string? clientCaseTitle)
            => new(clientId, clientName, legalEntityId, legalEntityName, clientCaseId, clientCaseTitle, null);

        public static TaskClientBinding Invalid(string error)
            => new(null, string.Empty, null, string.Empty, null, null, error);
    }

    private sealed record TaskBillingDecision(
        string Classification,
        int? ContractId,
        string? BillingMode,
        DateTime? DecisionAt,
        string? Error)
    {
        public static TaskBillingDecision Legacy()
            => new(TaskClientBillingClassification.Legacy, null, null, null, null);

        public static TaskBillingDecision Valid(string classification, int? contractId, string? billingMode)
            => new(classification, contractId, billingMode, DateTime.UtcNow, null);

        public static TaskBillingDecision Invalid(string error)
            => new(TaskClientBillingClassification.Legacy, null, null, null, error);
    }
}

public class TaskJournalNotificationDto
{
    public string Id { get; set; } = string.Empty;
    public int TaskId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Tone { get; set; } = "blue";
    public string DueAt { get; set; } = string.Empty;
}
