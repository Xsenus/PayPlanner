using System.Globalization;
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
        "task", "budget", "contact", "document", "reminder", "control"
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
    private static readonly string[] ActiveTaskStatuses =
    [
        "new", "inProgress", "waiting", "onApproval", "approved", "revision", "overdueApproval"
    ];
    private static readonly ClientCaseStatus[] FinishedCaseStatuses =
    [
        ClientCaseStatus.Closed,
        ClientCaseStatus.Lost,
    ];
    private const string RestrictExecutorEditingSettingKey = "tasks.restrictExecutorEditing";
    private const string AllowExecutorDeadlineChangeRequestsSettingKey = "tasks.allowExecutorDeadlineChangeRequests";
    private const string CaseInactivityAutomationEnabledSettingKey = "tasks.automation.caseControl.enabled";
    private const string CaseInactivityAutomationDaysSettingKey = "tasks.automation.caseControl.inactiveDays";
    private const string CaseInactivityAutomationLimitSettingKey = "tasks.automation.caseControl.limit";
    private const string CaseInactivityAutomationDefaultAssigneeSettingKey = "tasks.automation.caseControl.defaultAssigneeUserId";
    private const string CaseInactivityAutomationEscalationDaysSettingKey = "tasks.automation.caseControl.escalationDays";
    private const string CaseInactivityAutomationEscalationUserSettingKey = "tasks.automation.caseControl.escalationUserId";
    private const string ContractorPaymentMarker = "[PayPlanner:TaskContractorPayment]";

    private readonly PaymentContext _db;
    private readonly INotificationService _notificationService;
    private readonly ICaseDocumentStorage _caseDocumentStorage;
    private readonly string _taskFilesRoot;

    public TasksController(
        PaymentContext db,
        INotificationService notificationService,
        IWebHostEnvironment? environment = null,
        ICaseDocumentStorage? caseDocumentStorage = null)
    {
        _db = db;
        _notificationService = notificationService;
        _caseDocumentStorage = caseDocumentStorage ?? new LocalCaseDocumentStorage(environment);
        _taskFilesRoot = Path.Combine(environment?.ContentRootPath ?? AppContext.BaseDirectory, "App_Data", "task-files");
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
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
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

        var query = BuildVisibleTasksQuery(access);

        if (!string.IsNullOrWhiteSpace(status) && !string.Equals(status, "all", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(status, "active", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(task => task.Status != "done" && task.Status != "cancelled");
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

        if (assigneeUserId.HasValue)
        {
            query = query.Where(task =>
                task.AssigneeUserId == assigneeUserId.Value ||
                task.Participants.Any(participant =>
                    participant.Role == "assignee" &&
                    participant.UserId == assigneeUserId.Value));
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
            var term = search.Trim().ToLower();
            query = query.Where(task =>
                task.ObjectName.ToLower().Contains(term) ||
                (task.Client != null && task.Client.Name.ToLower().Contains(term)) ||
                (task.LegalEntity != null && (
                    task.LegalEntity.ShortName.ToLower().Contains(term) ||
                    (task.LegalEntity.FullName ?? "").ToLower().Contains(term) ||
                    (task.LegalEntity.Inn ?? "").ToLower().Contains(term))) ||
                (task.ClientCase != null && task.ClientCase.Title.ToLower().Contains(term)) ||
                task.Title.ToLower().Contains(term) ||
                (task.Description ?? "").ToLower().Contains(term) ||
                task.AssigneeName.ToLower().Contains(term) ||
                task.Participants.Any(participant =>
                    participant.DisplayName.ToLower().Contains(term) ||
                    participant.Email.ToLower().Contains(term) ||
                    (participant.Resolution ?? "").ToLower().Contains(term)) ||
                task.AuthorName.ToLower().Contains(term) ||
                (task.Result ?? "").ToLower().Contains(term) ||
                task.TagsJson.ToLower().Contains(term));
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderBy(task => task.DueAt)
            .ThenBy(task => task.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return Ok(new PagedResponse<TaskJournalItemDto>
        {
            Items = items.Select(item => Map(item)).ToList(),
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
        var dueTasks = await BuildVisibleTasksQuery(access)
            .Where(task => task.Status != "done" && task.Status != "cancelled" && task.DueAt <= soon)
            .OrderBy(task => task.DueAt)
            .Take(20)
            .ToListAsync(ct);

        var completedSince = now.AddDays(-7);
        var completedTasks = await BuildVisibleTasksQuery(access)
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

    [HttpGet("assignees")]
    public async Task<ActionResult<IReadOnlyList<TaskJournalAssigneeDto>>> GetAssignees(CancellationToken ct)
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

        var users = await _db.Users
            .AsNoTracking()
            .Where(user => user.IsActive && user.IsApproved)
            .OrderBy(user => user.FullName)
            .ThenBy(user => user.Email)
            .Select(user => new TaskJournalAssigneeDto
            {
                Id = user.Id,
                Name = user.FullName == "" ? user.Email : user.FullName,
                Email = user.Email,
                IsExternalContractor = user.IsExternalContractor,
            })
            .ToListAsync(ct);

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
        var requestedDefaultAssigneeUserId = request.CaseInactivityDefaultAssigneeUserId.GetValueOrDefault();
        var defaultAssigneeUserId = requestedDefaultAssigneeUserId > 0
            ? requestedDefaultAssigneeUserId.ToString()
            : "0";
        var requestedEscalationUserId = request.CaseInactivityEscalationUserId.GetValueOrDefault();
        var escalationUserId = requestedEscalationUserId > 0
            ? requestedEscalationUserId.ToString()
            : "0";

        await UpsertSystemSettingAsync(RestrictExecutorEditingSettingKey, request.RestrictExecutorEditing ? "true" : "false", now, ct);
        await UpsertSystemSettingAsync(AllowExecutorDeadlineChangeRequestsSettingKey, request.AllowExecutorDeadlineChangeRequests ? "true" : "false", now, ct);
        await UpsertSystemSettingAsync(CaseInactivityAutomationEnabledSettingKey, request.CaseInactivityAutomationEnabled ? "true" : "false", now, ct);
        await UpsertSystemSettingAsync(CaseInactivityAutomationDaysSettingKey, inactivityDays.ToString(), now, ct);
        await UpsertSystemSettingAsync(CaseInactivityAutomationLimitSettingKey, inactivityLimit.ToString(), now, ct);
        await UpsertSystemSettingAsync(CaseInactivityAutomationDefaultAssigneeSettingKey, defaultAssigneeUserId, now, ct);
        await UpsertSystemSettingAsync(CaseInactivityAutomationEscalationDaysSettingKey, escalationDays.ToString(), now, ct);
        await UpsertSystemSettingAsync(CaseInactivityAutomationEscalationUserSettingKey, escalationUserId, now, ct);
        await _db.SaveChangesAsync(ct);

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
        var inactivityCutoff = now.AddDays(-settings.CaseInactivityDays);
        var escalationCutoff = now.AddDays(-settings.CaseInactivityEscalationDays);

        var visibleCases = _db.ClientCases
            .AsNoTracking()
            .ApplyClientCaseVisibility(access.Visibility)
            .Where(clientCase =>
                ((clientCase.ClientId.HasValue && clientCase.Client != null) ||
                 (clientCase.LegalEntityId.HasValue && clientCase.LegalEntity != null)) &&
                !FinishedCaseStatuses.Contains(clientCase.Status))
            .Select(clientCase => new
            {
                clientCase.Id,
                clientCase.CreatedAt,
                clientCase.LastViewedAt,
                clientCase.ActivityConfirmedAt,
                clientCase.InactivityRemindersEnabled,
                ActiveTaskCount = _db.TaskJournalItems.Count(task =>
                    task.ClientCaseId == clientCase.Id &&
                    ActiveTaskStatuses.Contains(task.Status)),
                LastTaskAt = _db.TaskJournalItems
                    .IgnoreQueryFilters()
                    .Where(task => task.ClientCaseId == clientCase.Id)
                    .Max(task => (DateTime?)(task.UpdatedAt ?? task.CreatedAt)),
            });

        var candidateQuery = visibleCases.Where(item =>
            item.ActiveTaskCount == 0 &&
            (item.LastTaskAt ?? item.CreatedAt) <= inactivityCutoff &&
            (item.LastViewedAt ?? item.CreatedAt) <= inactivityCutoff &&
            (item.ActivityConfirmedAt ?? item.CreatedAt) <= inactivityCutoff &&
            item.InactivityRemindersEnabled);

        var escalationCandidateQuery = visibleCases.Where(item =>
            item.ActiveTaskCount == 0 &&
            (item.LastTaskAt ?? item.CreatedAt) <= escalationCutoff &&
            (item.LastViewedAt ?? item.CreatedAt) <= escalationCutoff &&
            (item.ActivityConfirmedAt ?? item.CreatedAt) <= escalationCutoff &&
            item.InactivityRemindersEnabled);

        var auditQuery = _db.ObjectAuditEvents
            .AsNoTracking()
            .Where(item =>
                item.ObjectType == "client-case" &&
                item.Action == "Automation" &&
                item.MetadataJson != null &&
                item.MetadataJson.Contains("caseInactivityControlTaskCreated"));

        var lastCreated = await auditQuery
            .OrderByDescending(item => item.CreatedAt)
            .Select(item => new { item.CreatedAt, item.Description })
            .FirstOrDefaultAsync(ct);

        var createdSince = now.AddDays(-7);
        return Ok(new TaskJournalAutomationDiagnosticsDto
        {
            Enabled = settings.CaseInactivityAutomationEnabled,
            InactivityDays = settings.CaseInactivityDays,
            EscalationDays = settings.CaseInactivityEscalationDays,
            Limit = settings.CaseInactivityLimit,
            DefaultAssigneeUserId = settings.CaseInactivityDefaultAssigneeUserId,
            DefaultAssigneeName = defaultAssignee is null ? string.Empty : BuildUserDisplayName(defaultAssignee.FullName, defaultAssignee.Email),
            EscalationUserId = settings.CaseInactivityEscalationUserId,
            EscalationUserName = escalationUser is null ? string.Empty : BuildUserDisplayName(escalationUser.FullName, escalationUser.Email),
            VisibleCandidateCount = settings.CaseInactivityAutomationEnabled ? await candidateQuery.CountAsync(ct) : 0,
            VisibleEscalationCandidateCount = settings.CaseInactivityAutomationEnabled ? await escalationCandidateQuery.CountAsync(ct) : 0,
            CreatedTaskCountLast7Days = await auditQuery.CountAsync(item => item.CreatedAt >= createdSince, ct),
            LastCreatedAt = lastCreated is null ? null : ToIso(lastCreated.CreatedAt),
            LastCreatedDescription = lastCreated?.Description,
        });
    }

    [HttpGet("calendar.ics")]
    public async Task<IActionResult> ExportCalendar(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
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

        var tasks = await query
            .OrderBy(task => task.DueAt)
            .ThenBy(task => task.Id)
            .Take(1000)
            .ToListAsync(ct);

        var calendar = BuildIcsCalendar(tasks);
        return File(
            Encoding.UTF8.GetBytes(calendar),
            "text/calendar; charset=utf-8",
            "task-calendar.ics");
    }

    [HttpGet("automation/case-candidates")]
    public async Task<ActionResult<IReadOnlyList<TaskJournalCaseAutomationCandidateDto>>> GetAutomationCaseCandidates(
        [FromQuery] int? inactiveDays = null,
        [FromQuery] int? limit = null,
        [FromQuery] int? escalationDays = null,
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
        var effectiveEscalationDays = Math.Clamp(escalationDays ?? settings.CaseInactivityEscalationDays, effectiveInactiveDays, 365);
        var now = DateTime.UtcNow;
        var cutoff = now.AddDays(-effectiveInactiveDays);

        var candidates = await _db.ClientCases
            .AsNoTracking()
            .ApplyClientCaseVisibility(access.Visibility)
            .Where(clientCase =>
                ((clientCase.ClientId.HasValue && clientCase.Client != null) ||
                 (clientCase.LegalEntityId.HasValue && clientCase.LegalEntity != null)) &&
                !FinishedCaseStatuses.Contains(clientCase.Status))
            .Select(clientCase => new
            {
                clientCase.Id,
                clientCase.Title,
                clientCase.CreatedAt,
                clientCase.ClientId,
                ClientName = clientCase.Client != null ? clientCase.Client.Name : string.Empty,
                clientCase.LegalEntityId,
                clientCase.LastViewedAt,
                clientCase.ActivityConfirmedAt,
                clientCase.InactivityRemindersEnabled,
                LegalEntityName = clientCase.LegalEntity != null
                    ? (clientCase.LegalEntity.ShortName != string.Empty
                        ? clientCase.LegalEntity.ShortName
                        : clientCase.LegalEntity.FullName ?? string.Empty)
                    : string.Empty,
                ActiveTaskCount = _db.TaskJournalItems.Count(task =>
                    task.ClientCaseId == clientCase.Id &&
                    ActiveTaskStatuses.Contains(task.Status)),
                CompletedTaskCount = _db.TaskJournalItems.Count(task =>
                    task.ClientCaseId == clientCase.Id &&
                    (task.Status == "done" || task.Status == "cancelled")),
                LastTaskAt = _db.TaskJournalItems
                    .IgnoreQueryFilters()
                    .Where(task => task.ClientCaseId == clientCase.Id)
                    .Max(task => (DateTime?)(task.UpdatedAt ?? task.CreatedAt)),
            })
            .Where(item => item.ActiveTaskCount == 0
                && (item.LastTaskAt ?? item.CreatedAt) <= cutoff
                && (item.LastViewedAt ?? item.CreatedAt) <= cutoff
                && (item.ActivityConfirmedAt ?? item.CreatedAt) <= cutoff
                && item.InactivityRemindersEnabled)
            .OrderBy(item => item.LastTaskAt ?? item.CreatedAt)
            .ThenBy(item => item.ClientName != string.Empty ? item.ClientName : item.LegalEntityName)
            .ThenBy(item => item.Title)
            .Take(effectiveLimit)
            .ToListAsync(ct);

        return Ok(candidates.Select(item =>
        {
            var lastActivity = item.CreatedAt;
            if (item.LastTaskAt.HasValue && item.LastTaskAt.Value > lastActivity)
            {
                lastActivity = item.LastTaskAt.Value;
            }

            if (item.LastViewedAt.HasValue && item.LastViewedAt.Value > lastActivity)
            {
                lastActivity = item.LastViewedAt.Value;
            }

            if (item.ActivityConfirmedAt.HasValue && item.ActivityConfirmedAt.Value > lastActivity)
            {
                lastActivity = item.ActivityConfirmedAt.Value;
            }

            var daysWithoutActiveTask = Math.Max(0, (int)Math.Floor((now - lastActivity).TotalDays));
            var isEscalated = daysWithoutActiveTask >= effectiveEscalationDays;
            var reason = item.LastTaskAt is null
                ? "По делу еще не было задач"
                : daysWithoutActiveTask >= effectiveInactiveDays
                    ? $"Нет активной задачи уже {daysWithoutActiveTask} дн."
                    : "Нет активной задачи";

            return new TaskJournalCaseAutomationCandidateDto
            {
                ClientId = item.ClientId,
                ClientName = item.ClientName,
                LegalEntityId = item.LegalEntityId,
                LegalEntityName = item.LegalEntityName,
                ClientCaseId = item.Id,
                ClientCaseTitle = item.Title,
                Reason = reason,
                ActiveTaskCount = item.ActiveTaskCount,
                CompletedTaskCount = item.CompletedTaskCount,
                DaysWithoutActiveTask = daysWithoutActiveTask,
                IsEscalated = isEscalated,
                LastTaskAt = item.LastTaskAt.HasValue ? ToIso(item.LastTaskAt.Value) : null,
                SuggestedTitle = "Проверить движение по делу",
                SuggestedDescription = BuildAutomationTaskDescription(isEscalated),
                SuggestedDueAt = ToIso(now.Date.AddDays(1).AddHours(9)),
                SuggestedPriority = ResolveAutomationPriority(daysWithoutActiveTask, effectiveInactiveDays, effectiveEscalationDays),
            };
        }).ToList());
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

        var clientCase = await _db.ClientCases
            .Include(item => item.Client)
            .Include(item => item.LegalEntity)
            .ApplyClientCaseVisibility(access.Visibility)
            .FirstOrDefaultAsync(item =>
                item.Id == clientCaseId &&
                ((item.ClientId.HasValue && item.Client != null) ||
                 (item.LegalEntityId.HasValue && item.LegalEntity != null)) &&
                !FinishedCaseStatuses.Contains(item.Status),
                ct);
        if (clientCase is null)
        {
            return NotFound(new { message = "Дело для автоконтроля не найдено или уже закрыто." });
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

        var lastTaskAt = await _db.TaskJournalItems
            .IgnoreQueryFilters()
            .Where(task => task.ClientCaseId == clientCase.Id)
            .MaxAsync(task => (DateTime?)(task.UpdatedAt ?? task.CreatedAt), ct);
        var settings = await GetTaskJournalSettingsAsync(ct);
        if (!settings.CaseInactivityAutomationEnabled || !clientCase.InactivityRemindersEnabled)
        {
            return BadRequest(new { message = "Автоконтроль для этого дела отключен." });
        }

        var lastActivity = ResolveLastCaseActivity(clientCase, lastTaskAt);
        var daysWithoutActiveTask = Math.Max(0, (int)Math.Floor((DateTime.UtcNow - lastActivity).TotalDays));
        if (daysWithoutActiveTask < settings.CaseInactivityDays)
        {
            return Conflict(new
            {
                message = "По делу недавно было движение. Контрольная задача пока не требуется.",
                daysWithoutActiveTask,
            });
        }

        var isEscalated = daysWithoutActiveTask >= settings.CaseInactivityEscalationDays;
        var assignee = await ResolveAutomationAssigneeAsync(clientCase, access, ct);
        var escalationUser = isEscalated
            ? await ResolveConfiguredAutomationUserAsync(settings.CaseInactivityEscalationUserId, ct)
            : null;
        var binding = BuildCaseBinding(clientCase);
        if (binding.Error is not null)
        {
            return NotFound(new { message = "Дело для автоконтроля не найдено или уже закрыто." });
        }

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
            DueAt = DateTime.UtcNow.Date.AddDays(1).AddHours(9),
            PlannedDurationMinutes = 60,
            Type = "control",
            Priority = ResolveAutomationPriority(daysWithoutActiveTask, settings.CaseInactivityDays, settings.CaseInactivityEscalationDays),
            Status = "new",
            Result = null,
            RemindersJson = SerializeList(["before1d", "atDue"], AllowedReminders, ["atDue"]),
            ChannelsJson = SerializeList(["inApp"], AllowedChannels, ["inApp"]),
            TagsJson = SerializeFreeList(["автоконтроль"]),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Participants = [ToParticipantEntity(assignee)],
        };
        AddEscalationObserver(entity, escalationUser, assignee.UserId, DateTime.UtcNow);

        AddTaskHistory(entity, access, "Задача создана автоматически из автоконтроля дела.");
        _db.TaskJournalItems.Add(entity);
        await _db.SaveChangesAsync(ct);
        return Created($"/api/tasks/{entity.Id}", Map(entity, binding));
    }

    [HttpPost("automation/case-candidates/tasks")]
    public async Task<ActionResult<TaskJournalAutomationCreateResultDto>> CreateAutomationTasksForCandidates(
        [FromQuery] int limit = 20,
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

        limit = Math.Clamp(limit, 1, 100);
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
        var cutoff = DateTime.UtcNow.AddDays(-settings.CaseInactivityDays);
        var escalationUser = await ResolveConfiguredAutomationUserAsync(settings.CaseInactivityEscalationUserId, ct);
        var candidates = await _db.ClientCases
            .Include(item => item.Client)
            .Include(item => item.LegalEntity)
            .ApplyClientCaseVisibility(access.Visibility)
            .Where(clientCase =>
                ((clientCase.ClientId.HasValue && clientCase.Client != null) ||
                 (clientCase.LegalEntityId.HasValue && clientCase.LegalEntity != null)) &&
                !FinishedCaseStatuses.Contains(clientCase.Status))
            .Select(clientCase => new
            {
                Case = clientCase,
                ActiveTaskCount = _db.TaskJournalItems.Count(task =>
                    task.ClientCaseId == clientCase.Id &&
                    ActiveTaskStatuses.Contains(task.Status)),
                LastTaskAt = _db.TaskJournalItems
                    .IgnoreQueryFilters()
                    .Where(task => task.ClientCaseId == clientCase.Id)
                    .Max(task => (DateTime?)(task.UpdatedAt ?? task.CreatedAt)),
            })
            .Where(item => item.ActiveTaskCount == 0
                && (item.LastTaskAt ?? item.Case.CreatedAt) <= cutoff
                && (item.Case.LastViewedAt ?? item.Case.CreatedAt) <= cutoff
                && (item.Case.ActivityConfirmedAt ?? item.Case.CreatedAt) <= cutoff
                && item.Case.InactivityRemindersEnabled)
            .OrderBy(item => item.LastTaskAt ?? item.Case.CreatedAt)
            .ThenBy(item => item.Case.Client != null
                ? item.Case.Client.Name
                : item.Case.LegalEntity != null
                    ? item.Case.LegalEntity.ShortName
                    : string.Empty)
            .ThenBy(item => item.Case.Title)
            .Take(effectiveLimit)
            .ToListAsync(ct);

        var created = new List<(TaskJournalItem Entity, TaskClientBinding Binding)>();
        foreach (var item in candidates)
        {
            var binding = BuildCaseBinding(item.Case);
            if (binding.Error is not null)
            {
                continue;
            }

            var assignee = await ResolveAutomationAssigneeAsync(item.Case, access, ct);
            var entity = BuildAutomationTaskEntity(item.Case, binding, access, assignee, item.LastTaskAt, settings, escalationUser);
            AddTaskHistory(entity, access, "Задача создана автоматически из массового автоконтроля дел.");
            _db.TaskJournalItems.Add(entity);
            created.Add((entity, binding));
        }

        if (created.Count > 0)
        {
            await _db.SaveChangesAsync(ct);
        }

        return Ok(new TaskJournalAutomationCreateResultDto
        {
            CreatedTasks = created.Select(item => Map(item.Entity, item.Binding)).ToList(),
            SkippedCount = candidates.Count - created.Count,
        });
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

        var task = await BuildVisibleTasksQuery(access).FirstOrDefaultAsync(item => item.Id == id, ct);
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
        _db.TaskJournalComments.Add(comment);
        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _notificationService.NotifyTaskCommentAsync(entity, comment, ct);

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
        if (reason is not null && reason.Length > 1000)
        {
            return BadRequest(new { message = "Причина переноса не должна превышать 1000 символов." });
        }

        var commentText = BuildDeadlineChangeRequestText(entity, access.DisplayName, request.RequestedDueAt, reason);
        var comment = CreateTaskComment(
            entity.Id,
            access,
            "system",
            commentText,
            JsonSerializer.Serialize(new
            {
                type = "deadlineChangeRequest",
                requestedDueAt = ToIso(request.RequestedDueAt),
                reason,
            }, JsonOptions));

        _db.TaskJournalComments.Add(comment);
        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return Created($"/api/tasks/{entity.Id}/comments/{comment.Id}", MapComment(comment));
    }

    [HttpGet("{id:int}/files")]
    public async Task<ActionResult<IReadOnlyList<TaskJournalFileDto>>> GetFiles(int id, CancellationToken ct)
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

        var files = await _db.TaskJournalFiles
            .AsNoTracking()
            .Where(item => item.TaskJournalItemId == id)
            .OrderBy(item => item.OriginalFileName)
            .ThenByDescending(item => item.Version)
            .ThenByDescending(item => item.Id)
            .ToListAsync(ct);

        return Ok(MapFiles(files));
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

        if (ShouldRestrictExecutorFullEdit(entity, access, await GetTaskJournalSettingsAsync(ct)))
        {
            return Forbid();
        }

        if (file is null || file.Length <= 0)
        {
            return BadRequest(new { message = "Выберите файл для загрузки." });
        }

        if (file.Length > MaxTaskFileBytes)
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
        var safeFileName = SanitizeFileName(file.FileName);
        var storedFileName = BuildStoredFileName(normalizedDocumentKey!, version, safeFileName);
        var relativePath = Path.Combine(id.ToString(), storedFileName);
        var fullPath = ResolveTaskFilePath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string? caseDocumentStoragePath = null;

        try
        {
            await using (var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await file.CopyToAsync(stream, ct);
            }

            var taskFile = new TaskJournalFile
            {
                TaskJournalItemId = id,
                DocumentKey = normalizedDocumentKey!,
                Version = version,
                OriginalFileName = safeFileName,
                StoredFileName = storedFileName,
                StoragePath = relativePath,
                ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
                SizeBytes = file.Length,
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
                await using (var caseStream = file.OpenReadStream())
                {
                    await _caseDocumentStorage.SaveAsync(caseDocumentStoragePath, caseStream, ct);
                }

                _db.ClientCaseDocuments.Add(new ClientCaseDocument
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
                });
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

            return Created($"/api/tasks/{entity.Id}/files/{taskFile.Id}", MapFile(taskFile, true));
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

        return NoContent();
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
        var statusValidation = ValidateStatusRules(nextStatus, NormalizeOptional(request.Result), participants.Items);
        if (statusValidation is not null)
        {
            return BadRequest(new { message = statusValidation });
        }

        var entity = new TaskJournalItem
        {
            ClientId = binding.ClientId,
            LegalEntityId = binding.LegalEntityId,
            ClientCaseId = binding.ClientCaseId,
            ObjectName = BuildObjectName(binding.OwnerName, binding.ClientCaseTitle),
            Title = request.Title.Trim(),
            Description = NormalizeOptional(request.Description),
            AssigneeUserId = participants.PrimaryAssigneeUserId,
            AssigneeName = participants.PrimaryAssigneeName,
            AuthorUserId = access.UserId,
            AuthorName = access.DisplayName,
            DueAt = request.DueAt,
            ApprovalDueAt = NormalizeApprovalDueAt(request.ApprovalDueAt),
            PlannedDurationMinutes = NormalizeTaskDuration(request.PlannedDurationMinutes),
            ContractorPaymentEnabled = request.ContractorPaymentEnabled,
            ContractorPaymentAmount = NormalizeMoneyAmount(request.ContractorPaymentAmount),
            ContractorPaymentDueDate = NormalizeDateOnly(request.ContractorPaymentDueDate),
            ContractorPaymentOnResult = request.ContractorPaymentOnResult,
            Type = NormalizeEnumValue(request.Type, AllowedTypes, "task"),
            Priority = NormalizeEnumValue(request.Priority, AllowedPriorities, "normal"),
            Status = nextStatus,
            Result = NormalizeOptional(request.Result),
            RemindersJson = SerializeList(request.Reminders, AllowedReminders, ["atDue"]),
            ChannelsJson = SerializeList(request.Channels, AllowedChannels, ["inApp"]),
            TagsJson = SerializeFreeList(request.Tags),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Participants = participants.Items.Select(ToParticipantEntity).ToList(),
        };

        AddTaskHistory(entity, access, "Задача создана.");
        _db.TaskJournalItems.Add(entity);
        await _db.SaveChangesAsync(ct);
        await SyncContractorPaymentAsync(entity, ct);
        await _db.SaveChangesAsync(ct);
        await _notificationService.NotifyTaskAssignedAsync(entity, access.DisplayName, access.UserId, ct);
        if (IsApprovalActiveStatus(entity.Status))
        {
            await _notificationService.NotifyTaskApprovalRequestedAsync(entity, access.DisplayName, access.UserId, ct);
        }
        await NotifyTaskScheduleConflictIfNeededAsync(entity, access, ct);
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
        if (!CanFullyModifyTask(entity, access, access.Permission?.CanEdit == true, settings))
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

        var participants = await ResolveParticipantsAsync(request, entity.AssigneeUserId ?? access.UserId, ct);
        if (participants.Error is not null)
        {
            return BadRequest(new { message = participants.Error });
        }

        var contractorPaymentValidation = await ValidateContractorPaymentRequestAsync(request, participants.PrimaryAssigneeUserId, ct);
        if (contractorPaymentValidation is not null)
        {
            return BadRequest(new { message = contractorPaymentValidation });
        }

        var previousStatus = entity.Status;
        var nextStatus = NormalizeEnumValue(request.Status, AllowedStatuses, "new");
        var approvalQueueRestarted = ShouldRestartApprovalQueue(previousStatus, nextStatus);
        participants = ResetApprovalQueueIfStartingApproval(previousStatus, nextStatus, participants);
        var statusValidation = ValidateStatusRules(nextStatus, NormalizeOptional(request.Result), participants.Items);
        if (statusValidation is not null)
        {
            return BadRequest(new { message = statusValidation });
        }

        var becameDone = !string.Equals(entity.Status, "done", StringComparison.OrdinalIgnoreCase)
            && string.Equals(nextStatus, "done", StringComparison.OrdinalIgnoreCase);
        var changeLabels = BuildTaskChangeLabels(entity, request, binding, participants, nextStatus);
        var changeComment = NormalizeOptional(request.ChangeComment);

        entity.ClientId = binding.ClientId;
        entity.LegalEntityId = binding.LegalEntityId;
        entity.ClientCaseId = binding.ClientCaseId;
        entity.ObjectName = BuildObjectName(binding.OwnerName, binding.ClientCaseTitle);
        entity.Title = request.Title.Trim();
        entity.Description = NormalizeOptional(request.Description);
        entity.AssigneeUserId = participants.PrimaryAssigneeUserId;
        entity.AssigneeName = participants.PrimaryAssigneeName;
        entity.DueAt = request.DueAt;
        entity.ApprovalDueAt = NormalizeApprovalDueAt(request.ApprovalDueAt);
        entity.PlannedDurationMinutes = NormalizeTaskDuration(request.PlannedDurationMinutes);
        entity.ContractorPaymentEnabled = request.ContractorPaymentEnabled;
        entity.ContractorPaymentAmount = NormalizeMoneyAmount(request.ContractorPaymentAmount);
        entity.ContractorPaymentDueDate = NormalizeDateOnly(request.ContractorPaymentDueDate);
        entity.ContractorPaymentOnResult = request.ContractorPaymentOnResult;
        entity.Type = NormalizeEnumValue(request.Type, AllowedTypes, "task");
        entity.Priority = NormalizeEnumValue(request.Priority, AllowedPriorities, "normal");
        entity.Status = nextStatus;
        entity.Result = NormalizeOptional(request.Result);
        entity.RemindersJson = SerializeList(request.Reminders, AllowedReminders, ["atDue"]);
        entity.ChannelsJson = SerializeList(request.Channels, AllowedChannels, ["inApp"]);
        entity.TagsJson = SerializeFreeList(request.Tags);
        entity.UpdatedAt = DateTime.UtcNow;

        _db.TaskJournalParticipants.RemoveRange(entity.Participants);
        entity.Participants.Clear();
        foreach (var participant in participants.Items.Select(ToParticipantEntity))
        {
            entity.Participants.Add(participant);
        }

        if (changeLabels.Count > 0)
        {
            AddTaskHistory(entity, access, $"Изменены поля: {string.Join(", ", changeLabels)}.");
        }

        if (approvalQueueRestarted)
        {
            AddTaskHistory(entity, access, "Очередь согласования запущена заново: все согласующие снова ожидают решения.");
        }

        TaskJournalComment? userComment = null;
        if (changeComment is not null)
        {
            userComment = CreateTaskComment(entity.Id, access, "comment", changeComment);
            _db.TaskJournalComments.Add(userComment);
        }

        await SyncContractorPaymentAsync(entity, ct);
        await _db.SaveChangesAsync(ct);
        if (changeLabels.Count > 0)
        {
            await _notificationService.NotifyTaskChangedAsync(entity, changeLabels, access.DisplayName, access.UserId, changeComment, ct);
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
        if (!CanUpdateTaskStatus(entity, access, settings))
        {
            return Forbid();
        }

        var nextStatus = NormalizeEnumValue(request.Status, AllowedStatuses, "new");
        var nextResult = NormalizeOptional(request.Result) ?? entity.Result;
        if (string.Equals(nextStatus, "done", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(nextResult))
        {
            return BadRequest(new { message = "Перед завершением задачи заполните результат." });
        }

        var statusValidation = ValidateStatusRules(nextStatus, nextResult, entity.Participants);
        if (statusValidation is not null)
        {
            return BadRequest(new { message = statusValidation });
        }

        var approvalQueueRestarted = ResetApprovalQueueIfStartingApproval(entity.Status, nextStatus, entity.Participants);
        var becameDone = !string.Equals(entity.Status, "done", StringComparison.OrdinalIgnoreCase)
            && string.Equals(nextStatus, "done", StringComparison.OrdinalIgnoreCase);
        var oldStatus = entity.Status;
        var oldResult = entity.Result;

        entity.Status = nextStatus;
        if (NormalizeOptional(request.Result) is { } result)
        {
            entity.Result = result;
        }
        entity.UpdatedAt = DateTime.UtcNow;

        var statusChanged = !string.Equals(oldStatus, entity.Status, StringComparison.OrdinalIgnoreCase);
        var resultChanged = !string.Equals(NormalizeOptional(oldResult), NormalizeOptional(entity.Result), StringComparison.Ordinal);
        if (statusChanged || resultChanged)
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

            if (approvalQueueRestarted)
            {
                parts.Add("очередь согласования запущена заново");
            }

            AddTaskHistory(entity, access, $"Изменено: {string.Join(", ", parts)}.");
        }

        await SyncContractorPaymentAsync(entity, ct);
        await _db.SaveChangesAsync(ct);
        if (statusChanged || resultChanged)
        {
            var changeLabels = new List<string>();
            if (statusChanged)
            {
                changeLabels.Add("Статус");
            }

            if (resultChanged)
            {
                changeLabels.Add("Результат");
            }

            if (approvalQueueRestarted || (!IsApprovalActiveStatus(oldStatus) && IsApprovalActiveStatus(entity.Status)))
            {
                await _notificationService.NotifyTaskApprovalRequestedAsync(entity, access.DisplayName, access.UserId, ct);
            }
            else
            {
                await _notificationService.NotifyTaskChangedAsync(entity, changeLabels, access.DisplayName, access.UserId, null, ct);
            }
        }

        if (becameDone)
        {
            await _notificationService.NotifyTaskCompletedAsync(entity, ct);
        }
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
        var notifications = new List<(TaskJournalItem Task, IReadOnlyList<string> ChangeLabels, bool BecameDone, bool ApprovalRequested)>();
        var now = DateTime.UtcNow;
        foreach (var entity in entities)
        {
            if (!CanFullyModifyTask(entity, access, access.Permission?.CanEdit == true, settings))
            {
                continue;
            }

            var changes = new List<string>();
            var approvalRequested = false;

            if (assignee is not null && ReplacePrimaryAssignee(entity, assignee, now))
            {
                changes.Add("Исполнитель");
            }

            if (nextPriority is not null && !string.Equals(entity.Priority, nextPriority, StringComparison.OrdinalIgnoreCase))
            {
                entity.Priority = nextPriority;
                changes.Add("Приоритет");
            }

            if (request.DueAt.HasValue && entity.DueAt != request.DueAt.Value)
            {
                entity.DueAt = request.DueAt.Value;
                changes.Add("Срок");
            }

            var becameDone = false;
            if (nextStatus is not null)
            {
                var oldStatus = entity.Status;
                var oldResult = entity.Result;
                var nextResult = NormalizeOptional(request.Result) ?? entity.Result;
                var statusValidation = ValidateStatusRules(nextStatus, nextResult, entity.Participants);
                if (statusValidation is not null)
                {
                    continue;
                }

                var approvalQueueRestarted = ResetApprovalQueueIfStartingApproval(entity.Status, nextStatus, entity.Participants);
                becameDone = !string.Equals(entity.Status, "done", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(nextStatus, "done", StringComparison.OrdinalIgnoreCase);
                entity.Status = nextStatus;
                approvalRequested = approvalQueueRestarted
                    || (!IsApprovalActiveStatus(oldStatus) && IsApprovalActiveStatus(entity.Status));
                if (NormalizeOptional(request.Result) is { } result)
                {
                    entity.Result = result;
                }

                if (!string.Equals(oldStatus, entity.Status, StringComparison.OrdinalIgnoreCase))
                {
                    changes.Add("Статус");
                }

                if (!string.Equals(NormalizeOptional(oldResult), NormalizeOptional(entity.Result), StringComparison.Ordinal))
                {
                    changes.Add("Результат");
                }

                if (approvalQueueRestarted)
                {
                    changes.Add("Очередь согласования");
                }
            }

            if (changes.Count == 0)
            {
                continue;
            }

            entity.UpdatedAt = now;
            AddTaskHistory(entity, access, $"Массовое изменение: {string.Join(", ", changes)}.");
            updated++;
            notifications.Add((entity, changes, becameDone, approvalRequested));
        }

        await _db.SaveChangesAsync(ct);
        foreach (var notification in notifications)
        {
            await _notificationService.NotifyTaskChangedAsync(
                notification.Task,
                notification.ChangeLabels,
                access.DisplayName,
                access.UserId,
                null,
                ct);

            if (notification.ApprovalRequested)
            {
                await _notificationService.NotifyTaskApprovalRequestedAsync(notification.Task, access.DisplayName, access.UserId, ct);
            }

            if (notification.BecameDone)
            {
                await _notificationService.NotifyTaskCompletedAsync(notification.Task, ct);
            }

            await NotifyTaskScheduleConflictIfNeededAsync(notification.Task, access, ct);
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

        if (!access.IsAdmin && access.Permission?.CanDelete != true)
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

        var deleted = 0;
        foreach (var entity in entities)
        {
            if (!CanFullyModifyTask(entity, access, access.Permission?.CanDelete == true, settings))
            {
                continue;
            }

            _db.TaskJournalItems.Remove(entity);
            deleted++;
        }

        if (deleted > 0)
        {
            await _db.SaveChangesAsync(ct);
        }

        return Ok(new
        {
            requested = ids.Count,
            updated = deleted,
            skipped = ids.Count - deleted,
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

        var resolution = NormalizeOptional(request.Resolution);
        participant.ApprovalStatus = decision;
        participant.Resolution = resolution;
        participant.RespondedAt = DateTime.UtcNow;
        participant.UpdatedAt = DateTime.UtcNow;

        if (string.Equals(decision, "revisionRequested", StringComparison.OrdinalIgnoreCase))
        {
            entity.Status = "revision";
        }
        else if (approvers.All(item => string.Equals(item.ApprovalStatus, "approved", StringComparison.OrdinalIgnoreCase)))
        {
            entity.Status = "approved";
        }
        else if (!string.Equals(entity.Status, "overdueApproval", StringComparison.OrdinalIgnoreCase))
        {
            entity.Status = "onApproval";
        }

        entity.UpdatedAt = DateTime.UtcNow;
        AddTaskHistory(
            entity,
            access,
            BuildApprovalHistoryText(decision, participant.DisplayName, resolution),
            new
            {
                kind = "approvalDecision",
                participantId = participant.Id,
                approverUserId = participant.UserId,
                approverName = participant.DisplayName,
                decision,
                resolution,
                resultingStatus = entity.Status,
                approvalDueAt = entity.ApprovalDueAt,
                respondedAt = participant.RespondedAt
            });
        await _db.SaveChangesAsync(ct);
        await _notificationService.NotifyTaskApprovalDecisionAsync(
            entity,
            participant,
            decision,
            access.DisplayName,
            access.UserId,
            resolution,
            ct);
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

        if (!access.IsAdmin && access.Permission?.CanDelete != true)
        {
            return Forbid();
        }

        var entity = await BuildVisibleTasksQuery(access).FirstOrDefaultAsync(item => item.Id == id, ct);
        if (entity is null)
        {
            return NotFound();
        }

        if (!CanFullyModifyTask(entity, access, access.Permission?.CanDelete == true, await GetTaskJournalSettingsAsync(ct)))
        {
            return Forbid();
        }

        _db.TaskJournalItems.Remove(entity);
        await _db.SaveChangesAsync(ct);
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
        var dayStart = start.Date;
        var dayEnd = dayStart.AddDays(1);

        var candidates = await _db.TaskJournalItems
            .Include(task => task.Participants)
            .Where(task =>
                task.Id != entity.Id &&
                ActiveTaskStatuses.Contains(task.Status) &&
                task.DueAt >= dayStart &&
                task.DueAt < dayEnd &&
                (
                    (task.AssigneeUserId.HasValue && assigneeUserIds.Contains(task.AssigneeUserId.Value)) ||
                    task.Participants.Any(participant =>
                        participant.Role == "assignee" &&
                        participant.UserId.HasValue &&
                        assigneeUserIds.Contains(participant.UserId.Value)) ||
                    (assigneeNames.Count > 0 && assigneeNames.Contains(task.AssigneeName.ToLower())) ||
                    task.Participants.Any(participant =>
                        participant.Role == "assignee" &&
                        assigneeNames.Contains(participant.DisplayName.ToLower()))
                ))
            .ToListAsync(ct);

        return candidates
            .Where(candidate => IntervalsOverlap(start, end, candidate.DueAt, GetTaskEndAt(candidate)))
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

    private static DateTime GetTaskEndAt(TaskJournalItem entity)
        => entity.DueAt.AddMinutes(NormalizeTaskDuration(entity.PlannedDurationMinutes));

    private static bool IntervalsOverlap(DateTime firstStart, DateTime firstEnd, DateTime secondStart, DateTime secondEnd)
        => firstStart < secondEnd && secondStart < firstEnd;

    private IQueryable<TaskJournalItem> BuildVisibleTasksQuery(TaskAccess access)
    {
        var query = _db.TaskJournalItems
            .Include(task => task.Client)
            .Include(task => task.LegalEntity)
            .Include(task => task.ClientCase)
            .Include(task => task.AssigneeUser)
            .Include(task => task.ContractorPayment)
            .Include(task => task.Participants)
                .ThenInclude(participant => participant.User)
            .AsQueryable();
        return query.ApplyTaskVisibility(access.Visibility);
    }

    private async Task<TaskAccess?> GetTaskAccessAsync(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return null;
        }

        var visibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Tasks, ct);
        if (visibility is null)
        {
            return null;
        }

        var isAdmin = visibility.IsAdmin;
        var permission = await _db.GetUserSectionPermissionAsync(userId.Value, RolePermissionCatalog.Tasks, ct);
        return new TaskAccess(userId.Value, User.GetDisplayName(), isAdmin, permission, visibility);
    }

    private async Task<TaskJournalSettingsDto> GetTaskJournalSettingsAsync(CancellationToken ct)
    {
        var settings = await _db.SystemSettings
            .AsNoTracking()
            .Where(item =>
                item.Key == RestrictExecutorEditingSettingKey ||
                item.Key == AllowExecutorDeadlineChangeRequestsSettingKey ||
                item.Key == CaseInactivityAutomationEnabledSettingKey ||
                item.Key == CaseInactivityAutomationDaysSettingKey ||
                item.Key == CaseInactivityAutomationLimitSettingKey ||
                item.Key == CaseInactivityAutomationDefaultAssigneeSettingKey ||
                item.Key == CaseInactivityAutomationEscalationDaysSettingKey ||
                item.Key == CaseInactivityAutomationEscalationUserSettingKey)
            .ToDictionaryAsync(item => item.Key, item => item.Value, ct);

        var defaultAssigneeUserId = ParseIntSetting(settings.GetValueOrDefault(CaseInactivityAutomationDefaultAssigneeSettingKey), 0);
        var escalationUserId = ParseIntSetting(settings.GetValueOrDefault(CaseInactivityAutomationEscalationUserSettingKey), 0);
        var inactivityDays = Math.Clamp(ParseIntSetting(settings.GetValueOrDefault(CaseInactivityAutomationDaysSettingKey), 14), 1, 365);
        return new TaskJournalSettingsDto
        {
            RestrictExecutorEditing = ParseBoolSetting(settings.GetValueOrDefault(RestrictExecutorEditingSettingKey), true),
            AllowExecutorDeadlineChangeRequests = ParseBoolSetting(settings.GetValueOrDefault(AllowExecutorDeadlineChangeRequestsSettingKey), true),
            CaseInactivityAutomationEnabled = ParseBoolSetting(settings.GetValueOrDefault(CaseInactivityAutomationEnabledSettingKey), true),
            CaseInactivityDays = inactivityDays,
            CaseInactivityLimit = Math.Clamp(ParseIntSetting(settings.GetValueOrDefault(CaseInactivityAutomationLimitSettingKey), 25), 1, 100),
            CaseInactivityDefaultAssigneeUserId = defaultAssigneeUserId > 0 ? defaultAssigneeUserId : null,
            CaseInactivityEscalationDays = Math.Clamp(ParseIntSetting(settings.GetValueOrDefault(CaseInactivityAutomationEscalationDaysSettingKey), 30), inactivityDays, 365),
            CaseInactivityEscalationUserId = escalationUserId > 0 ? escalationUserId : null,
        };
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

    private async Task<TaskParticipantsResolution> ResolveParticipantsAsync(
        UpsertTaskJournalItemRequest request,
        int fallbackUserId,
        CancellationToken ct)
    {
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

    private static DateTime ResolveLastCaseActivity(ClientCase clientCase, DateTime? lastTaskAt)
    {
        var lastActivity = clientCase.CreatedAt;
        if (lastTaskAt.HasValue && lastTaskAt.Value > lastActivity)
        {
            lastActivity = lastTaskAt.Value;
        }

        if (clientCase.LastViewedAt.HasValue && clientCase.LastViewedAt.Value > lastActivity)
        {
            lastActivity = clientCase.LastViewedAt.Value;
        }

        if (clientCase.ActivityConfirmedAt.HasValue && clientCase.ActivityConfirmedAt.Value > lastActivity)
        {
            lastActivity = clientCase.ActivityConfirmedAt.Value;
        }

        return lastActivity;
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

            if (NormalizeDateOnly(request.ContractorPaymentDueDate) is null)
            {
                return "Укажите дату оплаты исполнителю.";
            }
        }

        var changeComment = NormalizeOptional(request.ChangeComment);
        if (changeComment is { Length: > 4000 })
        {
            return "Комментарий к изменению не должен превышать 4000 символов.";
        }

        if (string.Equals(request.Status, "done", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(request.Result))
        {
            return "Перед завершением задачи заполните результат.";
        }

        return null;
    }

    private static TaskJournalItemDto Map(TaskJournalItem entity, TaskClientBinding? binding = null)
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
            ContractorPaymentId = entity.ContractorPaymentId,
            ContractorPaymentStatus = entity.ContractorPayment?.Status.ToString() ?? string.Empty,
            Type = entity.Type,
            Priority = entity.Priority,
            Status = ResolveTaskStatus(entity),
            Result = entity.Result ?? string.Empty,
            Reminders = DeserializeList(entity.RemindersJson),
            Channels = DeserializeList(entity.ChannelsJson),
            Tags = DeserializeList(entity.TagsJson),
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

    private static DateTime GetApprovalDueAt(TaskJournalItem entity)
        => entity.ApprovalDueAt ?? entity.DueAt;

    private static string ToIso(DateTime value)
        => DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("O");

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTime? NormalizeApprovalDueAt(DateTime? value)
        => value.HasValue && value.Value != default ? value.Value : null;

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

    private async Task SyncContractorPaymentAsync(TaskJournalItem entity, CancellationToken ct)
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

        var dueDate = NormalizeDateOnly(entity.ContractorPaymentDueDate)!.Value;
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
                Date = dueDate,
                PlannedDate = dueDate,
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
        payment.Date = dueDate;
        payment.PlannedDate = dueDate;
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
            || NormalizeMoneyAmount(entity.ContractorPaymentAmount) is null
            || NormalizeDateOnly(entity.ContractorPaymentDueDate) is null)
        {
            return false;
        }

        if (entity.ContractorPaymentOnResult && !string.Equals(entity.Status, "done", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return await _db.Users
            .AsNoTracking()
            .AnyAsync(user => user.Id == entity.AssigneeUserId.Value && user.IsExternalContractor, ct);
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

    private static string BuildIcsCalendar(IEnumerable<TaskJournalItem> tasks)
    {
        var now = DateTime.UtcNow;
        var builder = new StringBuilder();
        builder.AppendLine("BEGIN:VCALENDAR");
        builder.AppendLine("VERSION:2.0");
        builder.AppendLine("PRODID:-//PayPlanner//Task Journal//RU");
        builder.AppendLine("CALSCALE:GREGORIAN");
        builder.AppendLine("METHOD:PUBLISH");
        builder.AppendLine("X-WR-CALNAME:Журнал задач");

        foreach (var task in tasks)
        {
            var start = DateTime.SpecifyKind(task.DueAt, DateTimeKind.Utc);
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
            builder.AppendLine($"DTSTAMP:{FormatIcsDate(now)}");
            builder.AppendLine($"DTSTART:{FormatIcsDate(start)}");
            builder.AppendLine($"DTEND:{FormatIcsDate(end)}");
            builder.AppendLine($"SUMMARY:{EscapeIcsText(task.Title)}");
            builder.AppendLine($"DESCRIPTION:{EscapeIcsText(description)}");
            builder.AppendLine($"STATUS:{(task.Status == "done" ? "COMPLETED" : "CONFIRMED")}");
            builder.AppendLine("END:VEVENT");
        }

        builder.AppendLine("END:VCALENDAR");
        return builder.ToString().ReplaceLineEndings("\r\n");
    }

    private static string FormatIcsDate(DateTime value)
        => DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("yyyyMMdd'T'HHmmss'Z'");

    private static string EscapeIcsText(string? value)
        => (value ?? string.Empty)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(";", "\\;", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal)
            .Replace("\r\n", "\\n", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\n", StringComparison.Ordinal);

    private async Task<TaskParticipantDraft> ResolveAutomationAssigneeAsync(
        ClientCase clientCase,
        TaskAccess fallbackAccess,
        CancellationToken ct)
    {
        var responsibleUserId = clientCase.ResponsibleUserId ?? clientCase.Client?.ResponsibleUserId ?? clientCase.LegalEntity?.ResponsibleUserId;
        if (responsibleUserId.HasValue)
        {
            var responsible = await _db.Users
                .AsNoTracking()
                .Where(user => user.Id == responsibleUserId.Value && user.IsActive && user.IsApproved)
                .Select(user => new { user.Id, user.FullName, user.Email })
                .FirstOrDefaultAsync(ct);
            if (responsible is not null)
            {
                return new TaskParticipantDraft(
                    responsible.Id,
                    BuildUserDisplayName(responsible.FullName, responsible.Email),
                    responsible.Email,
                    "assignee",
                    0,
                    "pending",
                    null);
            }
        }

        return new TaskParticipantDraft(
            fallbackAccess.UserId,
            fallbackAccess.DisplayName,
            string.Empty,
            "assignee",
            0,
            "pending",
            null);
    }

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

    private static TaskJournalItem BuildAutomationTaskEntity(
        ClientCase clientCase,
        TaskClientBinding binding,
        TaskAccess access,
        TaskParticipantDraft assignee,
        DateTime? lastTaskAt,
        TaskJournalSettingsDto settings,
        User? escalationUser)
    {
        var lastActivity = ResolveLastCaseActivity(clientCase, lastTaskAt);
        var daysWithoutActiveTask = Math.Max(0, (int)Math.Floor((DateTime.UtcNow - lastActivity).TotalDays));
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
            DueAt = DateTime.UtcNow.Date.AddDays(1).AddHours(9),
            PlannedDurationMinutes = 60,
            Type = "control",
            Priority = ResolveAutomationPriority(daysWithoutActiveTask, settings.CaseInactivityDays, settings.CaseInactivityEscalationDays),
            Status = "new",
            Result = null,
            RemindersJson = SerializeList(["before1d", "atDue"], AllowedReminders, ["atDue"]),
            ChannelsJson = SerializeList(["inApp"], AllowedChannels, ["inApp"]),
            TagsJson = SerializeFreeList(["автоконтроль"]),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Participants = [ToParticipantEntity(assignee)],
        };

        AddEscalationObserver(task, isEscalated ? escalationUser : null, assignee.UserId, DateTime.UtcNow);
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

    private static string? ValidateStatusRules(
        string status,
        string? result,
        IEnumerable<TaskParticipantDraft> participants)
    {
        var approvers = participants
            .Where(participant => string.Equals(participant.Role, "approver", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return ValidateStatusRules(
            status,
            result,
            approvers.Count,
            approvers.Count == 0 || approvers.All(participant =>
            string.Equals(participant.ApprovalStatus, "approved", StringComparison.OrdinalIgnoreCase)));
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
        IEnumerable<TaskJournalParticipant> participants)
    {
        var approvers = participants
            .Where(participant => string.Equals(participant.Role, "approver", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return ValidateStatusRules(
            status,
            result,
            approvers.Count,
            approvers.Count == 0 || approvers.All(participant =>
                string.Equals(participant.ApprovalStatus, "approved", StringComparison.OrdinalIgnoreCase)));
    }

    private static string? ValidateStatusRules(string status, string? result, int approverCount, bool allApproversApproved)
    {
        if (string.Equals(status, "done", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(result))
        {
            return "Перед завершением задачи заполните результат.";
        }

        if (string.Equals(status, "done", StringComparison.OrdinalIgnoreCase)
            && approverCount > 0
            && !allApproversApproved)
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
            MetadataJson = string.IsNullOrWhiteSpace(comment.MetadataJson) ? "{}" : comment.MetadataJson,
            CreatedAt = ToIso(comment.CreatedAt),
            UpdatedAt = comment.UpdatedAt.HasValue ? ToIso(comment.UpdatedAt.Value) : null,
        };

    private static IReadOnlyList<TaskJournalFileDto> MapFiles(IReadOnlyCollection<TaskJournalFile> files)
    {
        var latestVersions = files
            .GroupBy(file => file.DocumentKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Max(file => file.Version),
                StringComparer.OrdinalIgnoreCase);

        return files
            .Select(file => MapFile(file, latestVersions.TryGetValue(file.DocumentKey, out var latest) && file.Version == latest))
            .ToList();
    }

    private static TaskJournalFileDto MapFile(TaskJournalFile file, bool isLatestVersion)
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

    private static string BuildTaskCaseDocumentKey(int taskId, string taskDocumentKey)
    {
        var prefix = $"task-{taskId}-";
        var maxSuffixLength = Math.Max(1, 64 - prefix.Length);
        var suffix = taskDocumentKey.Length <= maxSuffixLength
            ? taskDocumentKey
            : taskDocumentKey[..maxSuffixLength];

        return prefix + suffix;
    }

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
        bool hasCapability,
        TaskJournalSettingsDto settings)
    {
        if (access.IsAdmin)
        {
            return true;
        }

        if (!hasCapability || ShouldRestrictExecutorFullEdit(entity, access, settings))
        {
            return false;
        }

        if (access.Visibility.SeesAll
            || string.Equals(access.Visibility.ViewScope, DataVisibilityScope.Department, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsTaskAuthor(entity, access.UserId) || IsTaskExecutor(entity, access.UserId);
    }

    private static bool CanUpdateTaskStatus(
        TaskJournalItem entity,
        TaskAccess access,
        TaskJournalSettingsDto settings)
        => access.IsAdmin
            || CanFullyModifyTask(entity, access, access.Permission?.CanEdit == true, settings)
            || CanPostTaskComment(entity, access);

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
        if (entity.DueAt != request.DueAt)
        {
            changes.Add($"Срок: {FormatHistoryDateTime(entity.DueAt)} -> {FormatHistoryDateTime(request.DueAt)}");
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

        return changes;
    }

    private static void AddIfChanged<T>(ICollection<string> changes, string label, T current, T next)
    {
        if (!EqualityComparer<T>.Default.Equals(current, next))
        {
            changes.Add($"{label}: {FormatChangeValue(current)} -> {FormatChangeValue(next)}");
        }
    }

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

    private static string BuildApprovalHistoryText(string decision, string approverName, string? resolution)
    {
        var text = string.Equals(decision, "approved", StringComparison.OrdinalIgnoreCase)
            ? $"Согласовано: {approverName}."
            : $"Возвращено на доработку: {approverName}.";

        return string.IsNullOrWhiteSpace(resolution)
            ? text
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

    private static string FormatHistoryDateTime(DateTime value)
        => DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("dd.MM.yyyy HH:mm");

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
        => participant.Role switch
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
