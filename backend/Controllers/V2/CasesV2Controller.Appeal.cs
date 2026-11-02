using System.Data;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;

namespace PayPlanner.Api.Controllers.V2;

public partial class CasesV2Controller
{
    private const string AppealFilingTaskTag = "case-appeal-filing";

    public sealed class ExpiringCaseAppealDto
    {
        public int CaseId { get; init; }
        public string CaseTitle { get; init; } = string.Empty;
        public string CaseNumber { get; init; } = string.Empty;
        public DateTime CourtDecisionAt { get; init; }
        public DateTime AppealDeadlineAt { get; init; }
        public string DecisionSummary { get; init; } = string.Empty;
    }

    public sealed class DecideCaseAppealRequest
    {
        public DateTime CourtDecisionAt { get; init; }
        public DateTime AppealDeadlineAt { get; init; }
        public string Choice { get; init; } = string.Empty;
        public string Comment { get; init; } = string.Empty;
    }

    public sealed class CaseAppealDecisionDto
    {
        public int Id { get; init; }
        public int CaseId { get; init; }
        public string Choice { get; init; } = string.Empty;
        public int? TaskId { get; init; }
        public DateTime DecidedAt { get; init; }
        public string DecidedByUserName { get; init; } = string.Empty;
    }

    /// <summary>Only the choice tied to the current court decision is shown; older choices stay in history.</summary>
    [HttpGet("{id:int}/appeal-decision")]
    public async Task<ActionResult<CaseAppealDecisionDto?>> GetCurrentCaseAppealDecision(int id, CancellationToken ct,
        [FromQuery] DateTime? expectedCourtDecisionAt = null, [FromQuery] DateTime? expectedAppealDeadlineAt = null)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }
        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return MissingCasesVisibilityResult();
        }

        var clientCase = await _db.ClientCases.AsNoTracking()
            .ApplyClientCaseVisibility(visibility)
            .Where(item => item.Id == id)
            .Select(item => new { item.CourtDecisionAt, item.AppealDeadlineAt })
            .FirstOrDefaultAsync(ct);
        if (clientCase is null)
        {
            return NotFound();
        }
        // Check the visible judicial act before returning even an empty decision.
        // Legacy readers may omit both dates; new readers bind the result to their card.
        if (expectedCourtDecisionAt.HasValue != expectedAppealDeadlineAt.HasValue)
        {
            return BadRequest("Передайте обе даты судебного акта для проверки.");
        }
        if (expectedCourtDecisionAt.HasValue &&
            (clientCase.CourtDecisionAt?.Date != expectedCourtDecisionAt.Value.Date ||
             clientCase.AppealDeadlineAt?.Date != expectedAppealDeadlineAt!.Value.Date))
        {
            return Conflict("Даты судебного акта изменились. Обновите карточку дела.");
        }
        if (!clientCase.CourtDecisionAt.HasValue)
        {
            return Ok(null);
        }

        var currentDate = clientCase.CourtDecisionAt.Value.Date;
        var decision = await _db.ClientCaseAppealDecisions.AsNoTracking()
            .Where(item => item.ClientCaseId == id && item.CourtDecisionDate == currentDate)
            .OrderByDescending(item => item.Id)
            .Select(item => new CaseAppealDecisionDto
            {
                Id = item.Id,
                CaseId = id,
                Choice = item.Choice,
                TaskId = item.TaskJournalItemId,
                DecidedAt = item.DecidedAt,
                DecidedByUserName = item.DecidedByUserName,
            })
            .FirstOrDefaultAsync(ct);
        return Ok(decision);
    }

    public sealed class CaseAppealReminderDto
    {
        public bool Sent { get; init; }
        public int RecipientUserId { get; init; }
    }

    /// <summary>
    /// The pending appeal choices in the last three calendar days before the
    /// deadline. Date-only court deadlines are compared in the user's timezone.
    /// </summary>
    [HttpGet("appeal-deadlines/expiring")]
    public async Task<ActionResult<IReadOnlyList<ExpiringCaseAppealDto>>> GetExpiringCaseAppeals(
        [FromQuery] DateOnly? localDate = null,
        CancellationToken ct = default)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }
        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return MissingCasesVisibilityResult();
        }

        var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct);
        var today = localDate ?? DateOnly.FromDateTime(CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone));
        if (today > DateOnly.MaxValue.AddDays(-4))
        {
            return BadRequest(new { message = "Укажите более раннюю дату." });
        }
        var start = today.ToDateTime(TimeOnly.MinValue);
        var end = today.AddDays(4).ToDateTime(TimeOnly.MinValue);

        var cases = await _db.ClientCases.AsNoTracking()
            .ApplyClientCaseVisibility(visibility)
            .Where(item => (item.Status == ClientCaseStatus.Open || item.Status == ClientCaseStatus.OnHold)
                && item.CourtDecisionAt.HasValue
                && item.AppealDeadlineAt.HasValue
                && item.AppealDeadlineAt.Value >= start
                && item.AppealDeadlineAt.Value < end
                && item.CourtDecisionAt.Value.Date <= start
                && item.CourtDecisionAt.Value.Date <= item.AppealDeadlineAt.Value.Date
                && !_db.ClientCaseAppealDecisions.Any(decision => decision.ClientCaseId == item.Id
                    && decision.CourtDecisionDate == item.CourtDecisionAt.Value.Date)
                && !_db.ClientCaseCourtComplaints.Any(complaint => complaint.ClientCaseId == item.Id
                    && complaint.ComplaintType == "appealComplaint" && !complaint.IsArchived)
                && !_db.TaskJournalItems.Any(task => task.ClientCaseId == item.Id
                    && task.Title == "Подготовить апелляционную жалобу"
                    && task.Status != "cancelled"
                    && task.CreatedAt >= item.CourtDecisionAt.Value.Date
                    && !_db.ClientCaseAppealDecisions.Any(decision => decision.TaskJournalItemId == task.Id
                        && decision.CourtDecisionDate != item.CourtDecisionAt.Value.Date)))
            .OrderBy(item => item.AppealDeadlineAt)
            .ThenBy(item => item.Id)
            .Select(item => new ExpiringCaseAppealDto
            {
                CaseId = item.Id,
                CaseTitle = item.Title,
                CaseNumber = item.CourtCaseNumber != "" ? item.CourtCaseNumber : item.CaseNumber,
                CourtDecisionAt = item.CourtDecisionAt!.Value,
                AppealDeadlineAt = item.AppealDeadlineAt!.Value,
            })
            .Take(10)
            .ToListAsync(ct);

        if (cases.Count == 0)
        {
            return Ok(cases);
        }

        var caseIds = cases.Select(item => item.CaseId).ToArray();
        var outcomes = await _db.ClientCaseCourtEvents.AsNoTracking()
            .Where(item => caseIds.Contains(item.ClientCaseId)
                && item.EventType == "decision"
                && (item.MovementStatus == "held" || item.MovementStatus == "completed")
                && item.ResultSummary != "")
            .OrderByDescending(item => item.ScheduledAt)
            .ThenByDescending(item => item.Id)
            .Select(item => new { item.ClientCaseId, item.ScheduledAt, item.ResultSummary })
            .ToListAsync(ct);
        var decisionDates = cases.ToDictionary(item => item.CaseId, item => item.CourtDecisionAt.Date);
        var outcomeByCase = outcomes
            .Where(item => decisionDates.TryGetValue(item.ClientCaseId, out var decisionDate)
                && item.ScheduledAt.Date == decisionDate)
            .GroupBy(item => item.ClientCaseId)
            .ToDictionary(group => group.Key, group => group.First().ResultSummary);

        return Ok(cases.Select(item => new ExpiringCaseAppealDto
        {
            CaseId = item.CaseId,
            CaseTitle = item.CaseTitle,
            CaseNumber = item.CaseNumber,
            CourtDecisionAt = item.CourtDecisionAt,
            AppealDeadlineAt = item.AppealDeadlineAt,
            DecisionSummary = outcomeByCase.GetValueOrDefault(item.CaseId) ?? string.Empty,
        }).ToList());
    }

    /// <summary>
    /// Records the lawyer's choice to appeal and creates one filing task. A
    /// decision not to appeal needs separate authority rules and is deliberately
    /// not accepted by this endpoint until those rules are agreed.
    /// </summary>
    [HttpPost("{id:int}/appeal-decision")]
    public async Task<ActionResult<CaseAppealDecisionDto>> DecideCaseAppeal(
        int id,
        [FromBody] DecideCaseAppealRequest? request,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }
        if (request is null || !string.Equals(request.Choice, "appeal", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { message = "Пока доступно только действие «Создать задачу на обжалование». Решение не обжаловать требует согласованного порядка утверждения." });
        }
        if (request.Comment?.Length > 1000)
        {
            return BadRequest(new { message = "Комментарий не должен превышать 1000 символов." });
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return MissingCasesVisibilityResult();
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var clientCase = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .Include(item => item.Client)
            .Include(item => item.LegalEntity)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }
        if (IsFinishedCaseStatus(clientCase.Status) || !clientCase.CourtDecisionAt.HasValue || !clientCase.AppealDeadlineAt.HasValue)
        {
            return Conflict(new { message = "Дело закрыто либо в нём нет даты решения и срока обжалования. Обновите карточку дела." });
        }

        var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct);
        var localToday = CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date;
        var decisionDate = clientCase.CourtDecisionAt.Value.Date;
        var deadlineDate = clientCase.AppealDeadlineAt.Value.Date;
        if (decisionDate != request.CourtDecisionAt.Date || deadlineDate != request.AppealDeadlineAt.Date
            || decisionDate > localToday || deadlineDate < decisionDate || deadlineDate < localToday)
        {
            return Conflict(new { message = "Даты решения или обжалования изменились либо срок уже истёк. Обновите карточку дела перед действием." });
        }
        if (await _db.ClientCaseAppealDecisions.AnyAsync(item => item.ClientCaseId == id && item.CourtDecisionDate == decisionDate, ct))
        {
            return Conflict(new { message = "Решение по этому судебному акту уже записано. Обновите карточку дела." });
        }
        if (await _db.ClientCaseCourtComplaints.AnyAsync(item => item.ClientCaseId == id
            && item.ComplaintType == "appealComplaint" && !item.IsArchived, ct))
        {
            return Conflict(new { message = "В деле уже есть апелляционная жалоба. Проверьте её перед созданием задачи." });
        }
        if (await _db.TaskJournalItems.AnyAsync(item => item.ClientCaseId == id
            && item.Title == "Подготовить апелляционную жалобу"
            && item.Status != "cancelled"
            && item.CreatedAt >= decisionDate
            && !_db.ClientCaseAppealDecisions.Any(decision => decision.TaskJournalItemId == item.Id
                && decision.CourtDecisionDate != decisionDate), ct))
        {
            return Conflict(new { message = "Задача на апелляционную жалобу уже существует. Проверьте её перед созданием новой." });
        }

        var actorId = User.GetUserId();
        var actorName = User.GetDisplayName();
        var assigneeId = clientCase.ResponsibleUserId
            ?? clientCase.Client?.ResponsibleUserId
            ?? clientCase.LegalEntity?.ResponsibleUserId
            ?? actorId;
        var assignee = assigneeId.HasValue
            ? await _db.Users.AsNoTracking()
                .Where(item => item.Id == assigneeId.Value && item.IsActive && item.IsApproved
                    && item.Role != null && (item.Role.Name == "admin"
                        || item.Role.Permissions.Any(permission => permission.Section == RolePermissionCatalog.Cases
                            && permission.CanView && permission.CanEdit)))
                .FirstOrDefaultAsync(ct)
            : null;
        if (assignee is null)
        {
            return Conflict(new { message = "Для задачи на обжалование нужен действующий ответственный с правом работать с судебными делами. Назначьте его по делу." });
        }
        var assigneeVisibility = (await _db.ResolveDataVisibilitiesForUsersAsync(
            [assignee.Id], RolePermissionCatalog.Cases, ct)).GetValueOrDefault(assignee.Id);
        if (assigneeVisibility is null || !await _db.ClientCases.AsNoTracking()
            .ApplyClientCaseVisibility(assigneeVisibility)
            .AnyAsync(item => item.Id == id, ct))
        {
            return Conflict(new { message = "Ответственный не видит это судебное дело. Откройте ему доступ или назначьте другого сотрудника." });
        }

        var now = DateTime.UtcNow;
        var assigneeName = string.IsNullOrWhiteSpace(assignee.FullName) ? assignee.Email : assignee.FullName.Trim();
        var ownerName = clientCase.Client?.Name ?? clientCase.LegalEntity?.ShortName ?? clientCase.Title;
        // Set the work deadline a week before the legal deadline when the
        // decision is made in time. A late decision must not create a task
        // whose internal work deadline has already passed.
        var assigneeTimeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, assignee.Id, ct);
        var plannedDueAt = CalendarTimeZoneHelper.FromUserLocalToUtc(deadlineDate.AddDays(-7).AddHours(9), assigneeTimeZone);
        var legalDueAt = CalendarTimeZoneHelper.FromUserLocalToUtc(deadlineDate.AddHours(18), assigneeTimeZone);
        var dueAt = plannedDueAt > now
            ? plannedDueAt
            : legalDueAt > now ? legalDueAt : now.AddMinutes(15);
        var task = new TaskJournalItem
        {
            ClientId = clientCase.ClientId,
            LegalEntityId = clientCase.LegalEntityId,
            ClientCaseId = id,
            ObjectName = Truncate($"{ownerName} - {clientCase.Title}", 300),
            Title = "Подготовить апелляционную жалобу",
            Description = $"По делу «{clientCase.Title}» вынесено решение {decisionDate:dd.MM.yyyy}. Срок обжалования: {deadlineDate:dd.MM.yyyy}. Внутренний срок подготовки — за 7 дней до него, если решение принято заранее. Подготовьте жалобу и проверьте комплект документов.",
            AssigneeUserId = assignee.Id,
            AssigneeName = assigneeName,
            AuthorUserId = actorId,
            AuthorName = actorName,
            DueAt = dueAt,
            Type = "document",
            Priority = plannedDueAt <= now || dueAt <= now.AddDays(1) ? "urgent" : "high",
            Status = "new",
            PlannedDurationMinutes = 60,
            RemindersJson = JsonSerializer.Serialize(new[] { "before3d", "before1d", "atDue", "dailyOverdue" }),
            ChannelsJson = JsonSerializer.Serialize(new[] { "inApp", "email" }),
            TagsJson = JsonSerializer.Serialize(new[] { AppealFilingTaskTag, $"{AppealFilingTaskTag}:{decisionDate:yyyyMMdd}" }),
            CreatedAt = now,
            UpdatedAt = now,
            Participants =
            [
                new TaskJournalParticipant
                {
                    UserId = assignee.Id,
                    Role = "assignee",
                    DisplayName = assigneeName,
                    Email = assignee.Email,
                    SortOrder = 0,
                    ApprovalStatus = "pending",
                    CreatedAt = now,
                    UpdatedAt = now,
                },
            ],
            Comments =
            [
                new TaskJournalComment
                {
                    AuthorUserId = actorId,
                    AuthorName = actorName,
                    Kind = "system",
                    Text = "Задача создана после решения обжаловать судебный акт.",
                    MetadataJson = "{}",
                    CreatedAt = now,
                    UpdatedAt = now,
                },
            ],
        };
        _db.TaskJournalItems.Add(task);
        var decision = new ClientCaseAppealDecision
        {
            ClientCaseId = id,
            CourtDecisionDate = decisionDate,
            AppealDeadlineDate = deadlineDate,
            Choice = "appeal",
            Comment = request.Comment?.Trim() ?? string.Empty,
            TaskJournalItem = task,
            DecidedByUserId = actorId,
            DecidedByUserName = actorName,
            DecidedAt = now,
        };
        _db.ClientCaseAppealDecisions.Add(decision);
        AppendCaseEvent(clientCase, $"Принято решение обжаловать судебный акт от {decisionDate:dd.MM.yyyy}. Задача на жалобу создана. Решение принял: {actorName}.");
        _db.SetAuditReason($"Решение об обжаловании по делу #{id}");
        try
        {
            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(ct);
            return Conflict(new { message = "Решение по этому судебному акту уже записано либо данные дела изменились. Обновите карточку дела." });
        }

        if (_notificationService is not null)
        {
            await _notificationService.NotifyTaskAssignedAsync(task, actorName, actorId ?? 0, ct);
        }
        await _realtimePublisher.PublishTaskJournalChangedAsync(ct);
        return Ok(new CaseAppealDecisionDto
        {
            Id = decision.Id,
            CaseId = id,
            Choice = decision.Choice,
            TaskId = task.Id,
            DecidedAt = decision.DecidedAt,
            DecidedByUserName = decision.DecidedByUserName,
        });
    }

    [HttpPost("{id:int}/appeal-reminder")]
    public async Task<ActionResult<CaseAppealReminderDto>> RemindCaseAppealAssignee(
        int id,
        [FromBody] DecideCaseAppealRequest? request,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }
        if (request is null)
        {
            return BadRequest(new { message = "Укажите даты судебного решения и срока обжалования." });
        }
        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return MissingCasesVisibilityResult();
        }
        var clientCase = await _db.ClientCases.AsNoTracking()
            .ApplyClientCaseVisibility(visibility)
            .Include(item => item.Client)
            .Include(item => item.LegalEntity)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }
        var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct);
        var localToday = CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date;
        if (IsFinishedCaseStatus(clientCase.Status)
            || clientCase.CourtDecisionAt?.Date != request.CourtDecisionAt.Date
            || clientCase.AppealDeadlineAt?.Date != request.AppealDeadlineAt.Date
            || clientCase.AppealDeadlineAt.Value.Date < localToday
            || clientCase.AppealDeadlineAt.Value.Date > localToday.AddDays(3)
            || await _db.ClientCaseAppealDecisions.AnyAsync(item => item.ClientCaseId == id
                && item.CourtDecisionDate == clientCase.CourtDecisionAt.Value.Date, ct))
        {
            return Conflict(new { message = "Срок или решение по делу изменились. Обновите карточку перед отправкой напоминания." });
        }
        var assigneeId = clientCase.ResponsibleUserId
            ?? clientCase.Client?.ResponsibleUserId
            ?? clientCase.LegalEntity?.ResponsibleUserId;
        if (!assigneeId.HasValue || assigneeId.Value == User.GetUserId())
        {
            return BadRequest(new { message = "Назначьте другого ответственного по делу, которому нужно отправить напоминание." });
        }
        var canDecideAppeal = await _db.Users.AsNoTracking().AnyAsync(item => item.Id == assigneeId.Value
            && item.IsActive && item.IsApproved && item.Role != null
            && (item.Role.Name == "admin" || item.Role.Permissions.Any(permission => permission.Section == RolePermissionCatalog.Cases
                && permission.CanView && permission.CanEdit)), ct);
        if (!canDecideAppeal)
        {
            return Conflict(new { message = "Ответственный не может работать с судебным делом. Назначьте юриста с правом редактирования." });
        }
        var recipients = await ResolveCaseNotificationRecipientIdsAsync(id, ct);
        if (!recipients.Contains(assigneeId.Value))
        {
            return Conflict(new { message = "Ответственный не имеет доступа к делу или его учётная запись неактивна." });
        }

        var deadlineDate = clientCase.AppealDeadlineAt.Value.Date;
        var decisionDate = clientCase.CourtDecisionAt.Value.Date;
        var notificationService = _notificationService ?? CreateFallbackNotificationService();
        var sent = await notificationService.NotifyAsync(new NotificationEventEnvelope(
            assigneeId.Value,
            UserNotificationTypes.CaseProceduralDeadline,
            $"case:{id}:appeal-reminder:{decisionDate:yyyyMMdd}:{assigneeId.Value}:{localToday:yyyyMMdd}",
            $"Срок обжалования истекает: {BuildCaseNotificationTitle(clientCase)}",
            $"По делу «{clientCase.Title}» решение вынесено {decisionDate:dd.MM.yyyy}. Срок обжалования — {deadlineDate:dd.MM.yyyy}. Проверьте необходимость жалобы и сообщите о решении.",
            "case",
            id,
            null,
            "amber",
            new { clientCaseId = id, courtDecisionAt = decisionDate, appealDeadlineAt = deadlineDate },
            Channels: [NotificationChannels.InApp, NotificationChannels.Toast, NotificationChannels.WebPush]), ct);
        return Ok(new CaseAppealReminderDto { Sent = sent, RecipientUserId = assigneeId.Value });
    }
}
