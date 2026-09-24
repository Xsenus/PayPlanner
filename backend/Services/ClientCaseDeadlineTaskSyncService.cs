using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.LegalIntegrations;

namespace PayPlanner.Api.Services;

public static class ClientCaseDeadlineTaskSyncService
{
    private const string DeadlineTag = "case-deadline";
    private const string AppealReviewTaskTitle = "Изучить решение, оценить обжалование";
    private const string CassationReviewTaskTitle = "Оценить кассацию, подготовить жалобу";
    private const string AppealObjectionTaskTitle = "Подготовить возражения на апелляцию";
    private const string AppealHearingFollowupTaskTitle = "Отслеживать назначение заседания";
    private const string RescheduledHearingTaskTitle = "Учесть новую дату, проверить готовность";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> ClosedTaskStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "done", "cancelled"
    };

    public static async Task<bool> SyncAsync(
        PaymentContext db,
        int clientCaseId,
        int? actorUserId,
        string actorName,
        CancellationToken ct)
    {
        var clientCase = await db.ClientCases
            .Include(item => item.Client)
            .Include(item => item.LegalEntity)
            .Include(item => item.ProceduralDeadlines)
            .Include(item => item.CourtEvents)
            .Include(item => item.CourtComplaints)
            .AsSplitQuery()
            .FirstOrDefaultAsync(item => item.Id == clientCaseId, ct);
        if (clientCase is null)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        var normalizedActorName = NormalizeActorName(actorName);
        var existingTasks = await db.TaskJournalItems
            .Include(item => item.Participants)
            .Where(item => item.ClientCaseId == clientCase.Id && item.TagsJson.Contains(DeadlineTag))
            .ToListAsync(ct);

        if (IsFinishedCaseStatus(clientCase.Status))
        {
            var closedTaskCount = await ClientCaseTaskSyncService.CloseActiveTasksForClosedCaseAsync(
                db,
                clientCase.Id,
                actorUserId,
                normalizedActorName,
                ct);
            await db.SaveChangesAsync(ct);
            return closedTaskCount > 0;
        }

        var owner = ResolveOwner(clientCase);
        if (owner is null)
        {
            return false;
        }

        var taskJournalChanged = false;
        var assignee = await ResolveAssigneeAsync(db, clientCase, actorUserId, ct);
        var assigneeUserId = assignee?.Id ?? actorUserId;
        var assigneeName = assignee is null ? normalizedActorName : DisplayName(assignee);
        var assigneeEmail = assignee?.Email ?? string.Empty;
        foreach (var deadline in BuildDeadlines(clientCase))
        {
            var tag = BuildDeadlineTag(deadline.Key);
            var activeTask = existingTasks
                .Where(item => HasTag(item, tag))
                .OrderBy(item => ClosedTaskStatuses.Contains(item.Status))
                .ThenByDescending(item => item.UpdatedAt ?? item.CreatedAt)
                .FirstOrDefault();

            if (!deadline.DueAt.HasValue)
            {
                if (activeTask is not null && !ClosedTaskStatuses.Contains(activeTask.Status))
                {
                    var writReceived = deadline.Key == "writ-obtain" && clientCase.ExecutiveWritReceivedAt.HasValue;
                    var writSubmitted = deadline.Key == "writ-submit" && clientCase.ExecutiveWritSubmittedAt.HasValue;
                    var appealHearingScheduled = deadline.CompleteOnHearing;
                    var isAppealComplaintTask = deadline.Key.StartsWith("appeal-followup-", StringComparison.Ordinal)
                        || deadline.Key.StartsWith("appeal-objection-", StringComparison.Ordinal);
                    var isCourtHearing = deadline.TaskType == "courtHearing";
                    activeTask.Status = writReceived || writSubmitted || appealHearingScheduled ? "done" : "cancelled";
                    activeTask.Result = writReceived
                        ? $"Исполнительный лист получен {clientCase.ExecutiveWritReceivedAt!.Value:dd.MM.yyyy}."
                        : writSubmitted
                            ? $"Исполнительный лист подан {clientCase.ExecutiveWritSubmittedAt!.Value:dd.MM.yyyy} в {(clientCase.ExecutiveWritSubmittedTo == "bank" ? "банк" : "ФССП")}."
                        : appealHearingScheduled
                            ? "Заседание апелляционной инстанции назначено. Проверьте его дату в карточке дела."
                        : isAppealComplaintTask
                            ? "Жалоба или заседание больше не требуют этого действия. Проверьте карточку дела."
                        : isCourtHearing
                            ? "Судебное заседание отменено, перенесено или больше не активно."
                            : "Срок удален из карточки дела.";
                    activeTask.UpdatedAt = now;
                    AddHistory(db, activeTask, actorUserId, normalizedActorName, writReceived
                        ? "Получение исполнительного листа отмечено в карточке дела, задача выполнена."
                        : writSubmitted
                            ? "Подача исполнительного листа отмечена в карточке дела, задача выполнена."
                        : appealHearingScheduled
                            ? "Заседание апелляционной инстанции назначено, задача отслеживания выполнена."
                        : isAppealComplaintTask
                            ? "Связанная жалоба или заседание изменены, задача отменена."
                        : isCourtHearing
                            ? "Судебное заседание отменено, перенесено или завершено, связанная задача отменена."
                            : "Срок удален из карточки дела, контрольная задача отменена.", now);
                    taskJournalChanged = true;
                }
                else if (deadline.Key == "writ-submit" && clientCase.ExecutiveWritSubmittedAt.HasValue
                    && activeTask?.Status == "done"
                    && activeTask.Result?.StartsWith("Исполнительный лист подан", StringComparison.Ordinal) == true)
                {
                    var correctedResult = $"Исполнительный лист подан {clientCase.ExecutiveWritSubmittedAt.Value:dd.MM.yyyy} в {(clientCase.ExecutiveWritSubmittedTo == "bank" ? "банк" : "ФССП")}.";
                    if (!string.Equals(activeTask.Result, correctedResult, StringComparison.Ordinal))
                    {
                        activeTask.Result = correctedResult;
                        activeTask.UpdatedAt = now;
                        AddHistory(db, activeTask, actorUserId, normalizedActorName, "Сведения о подаче исполнительного листа исправлены в карточке дела.", now);
                        taskJournalChanged = true;
                    }
                }

                continue;
            }

            var dueAt = NormalizeDueAt(deadline.DueAt.Value, deadline.DateOnly);
            if (deadline.TaskType == "courtHearing"
                && activeTask?.Status == "done"
                && (activeTask.DueAt == dueAt
                    || (deadline.HearingAt.HasValue
                        && activeTask.DueAt == NormalizeDueAt(deadline.HearingAt.Value, deadline.DateOnly))))
            {
                // A completed preparation task is not recreated on every case sync.
                // The second comparison covers tasks completed before the deadline moved three days earlier.
                continue;
            }
            if (deadline.Title is AppealReviewTaskTitle or CassationReviewTaskTitle
                && activeTask?.Status == "done"
                && activeTask.DueAt == dueAt
                && string.Equals(activeTask.Description, BuildDescription(clientCase, deadline), StringComparison.Ordinal))
            {
                // Saving the same court decision must not recreate completed review work.
                continue;
            }
            if (deadline.Key is "writ-obtain" or "writ-submit"
                && activeTask?.Status == "done"
                && activeTask.DueAt == dueAt
                && !(deadline.Key == "writ-obtain"
                    && !clientCase.ExecutiveWritReceivedAt.HasValue
                    && activeTask.Result?.StartsWith("Исполнительный лист получен", StringComparison.Ordinal) == true)
                && !(deadline.Key == "writ-submit"
                    && !clientCase.ExecutiveWritSubmittedAt.HasValue
                    && activeTask.Result?.StartsWith("Исполнительный лист подан", StringComparison.Ordinal) == true))
            {
                // Repeated synchronization must not reopen a completed writ action.
                continue;
            }
            if (deadline.Key.StartsWith("appeal-followup-", StringComparison.Ordinal)
                && activeTask?.Status == "done"
                && activeTask.DueAt == dueAt)
            {
                continue;
            }
            if (deadline.Key.StartsWith("appeal-objection-", StringComparison.Ordinal)
                && activeTask?.Status == "done"
                && activeTask.DueAt == dueAt)
            {
                continue;
            }
            var taskDeadline = deadline.TaskType == "courtHearing"
                && activeTask is not null
                && (activeTask.DueAt != dueAt || activeTask.Title == RescheduledHearingTaskTitle)
                    ? deadline with { Title = RescheduledHearingTaskTitle }
                    : deadline;
            if (activeTask is null || ClosedTaskStatuses.Contains(activeTask.Status))
            {
                var task = BuildTask(
                    clientCase,
                    owner.Value,
                    taskDeadline,
                    dueAt,
                    assigneeUserId,
                    assigneeName,
                    assigneeEmail,
                    actorUserId,
                    normalizedActorName,
                    now);
                db.TaskJournalItems.Add(task);
                existingTasks.Add(task);
                taskJournalChanged = true;
                continue;
            }

            var changed = false;
            changed |= SetIfChanged(activeTask, static (task, value) => task.ObjectName = value, activeTask.ObjectName, BuildObjectName(owner.Value.Name, clientCase.Title));
            changed |= SetIfChanged(activeTask, static (task, value) => task.Title = value, activeTask.Title, taskDeadline.Title);
            changed |= SetIfChanged(activeTask, static (task, value) => task.Description = value, activeTask.Description ?? string.Empty, BuildDescription(clientCase, taskDeadline));
            changed |= SetIfChanged(activeTask, static (task, value) => task.Type = value, activeTask.Type, deadline.TaskType);
            changed |= SetIfChanged(activeTask, static (task, value) => task.Priority = value, activeTask.Priority, ResolvePriority(dueAt, now));
            changed |= SetIfChanged(activeTask, static (task, value) => task.Status = value, activeTask.Status, activeTask.Status == "cancelled" ? "new" : activeTask.Status);
            changed |= SetIfChanged(activeTask, static (task, value) => task.RemindersJson = value, activeTask.RemindersJson, SerializeList(["before7d", "before3d", "before1d", "atDue", "dailyOverdue"]));
            changed |= SetIfChanged(activeTask, static (task, value) => task.ChannelsJson = value, activeTask.ChannelsJson, SerializeList(["inApp", "email"]));
            changed |= SetIfChanged(activeTask, static (task, value) => task.TagsJson = value, activeTask.TagsJson, SerializeList([DeadlineTag, tag, deadline.CategoryTag]));

            if (activeTask.DueAt != dueAt)
            {
                activeTask.DueAt = dueAt;
                changed = true;
            }

            if (activeTask.ClientId != owner.Value.ClientId || activeTask.LegalEntityId != owner.Value.LegalEntityId)
            {
                activeTask.ClientId = owner.Value.ClientId;
                activeTask.LegalEntityId = owner.Value.LegalEntityId;
                changed = true;
            }

            if (activeTask.AssigneeUserId != assigneeUserId || activeTask.AssigneeName != assigneeName)
            {
                activeTask.AssigneeUserId = assigneeUserId;
                activeTask.AssigneeName = assigneeName;
                UpsertPrimaryAssignee(activeTask, assigneeUserId, assigneeName, assigneeEmail, now);
                changed = true;
            }

            if (changed)
            {
                activeTask.UpdatedAt = now;
                AddHistory(db, activeTask, actorUserId, normalizedActorName, deadline.TaskType == "courtHearing"
                    ? "Задача по судебному заседанию синхронизирована с карточкой суда."
                    : "Контрольная задача синхронизирована со сроками карточки дела.", now);
                taskJournalChanged = true;
            }
        }

        await db.SaveChangesAsync(ct);
        return taskJournalChanged;
    }

    private static TaskJournalItem BuildTask(
        ClientCase clientCase,
        CaseOwner owner,
        CaseDeadline deadline,
        DateTime dueAt,
        int? assigneeUserId,
        string assigneeName,
        string assigneeEmail,
        int? actorUserId,
        string actorName,
        DateTime now)
    {
        var tag = BuildDeadlineTag(deadline.Key);
        var task = new TaskJournalItem
        {
            ClientId = owner.ClientId,
            LegalEntityId = owner.LegalEntityId,
            ClientCaseId = clientCase.Id,
            ObjectName = BuildObjectName(owner.Name, clientCase.Title),
            Title = deadline.Title,
            Description = BuildDescription(clientCase, deadline),
            AssigneeUserId = assigneeUserId,
            AssigneeName = assigneeName,
            AuthorUserId = actorUserId ?? assigneeUserId,
            AuthorName = actorName,
            DueAt = dueAt,
            PlannedDurationMinutes = deadline.DateOnly ? 60 : Math.Max(30, deadline.DurationMinutes),
            Type = deadline.TaskType,
            Priority = ResolvePriority(dueAt, now),
            Status = "new",
            Result = null,
            RemindersJson = SerializeList(["before7d", "before3d", "before1d", "atDue", "dailyOverdue"]),
            ChannelsJson = SerializeList(["inApp", "email"]),
            TagsJson = SerializeList([DeadlineTag, tag, deadline.CategoryTag]),
            CreatedAt = now,
            UpdatedAt = now,
        };

        UpsertPrimaryAssignee(task, assigneeUserId, assigneeName, assigneeEmail, now);
        task.Comments.Add(CreateHistory(task.Id, actorUserId, actorName, deadline.TaskType == "courtHearing"
            ? "Задача создана автоматически после назначения судебного заседания."
            : deadline.Key.StartsWith("appeal-followup-", StringComparison.Ordinal)
                || deadline.Key.StartsWith("appeal-objection-", StringComparison.Ordinal)
                ? "Задача создана автоматически по поданной апелляционной жалобе."
            : "Контрольная задача создана из процессуального срока карточки дела.", now));
        return task;
    }

    private static IReadOnlyList<CaseDeadline> BuildDeadlines(ClientCase clientCase)
    {
        if (ClientCaseStageClassifier.IsPretrial(clientCase))
        {
            return
            [
                CreateDeadline("due", "Крайний срок по делу", clientCase.DueDate, true, 60)
            ];
        }

        var result = new List<CaseDeadline>();
        var separateDeadlineTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var appealDecisionAt = FindAppealDecisionAt(clientCase);
        foreach (var deadline in clientCase.ProceduralDeadlines.OrderBy(item => item.DueAt).ThenBy(item => item.Id))
        {
            separateDeadlineTypes.Add(deadline.DeadlineType);
            var isClosed = string.Equals(deadline.Status, "closed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(deadline.Status, "cancelled", StringComparison.OrdinalIgnoreCase)
                || string.Equals(deadline.Status, "archived", StringComparison.OrdinalIgnoreCase);
            result.Add(CreateDeadline(
                $"entity-{deadline.Id}",
                ResolveReviewTaskTitle(clientCase, deadline, appealDecisionAt),
                isClosed ? null : deadline.DueAt,
                deadline.AllDay,
                deadline.AllDay ? 60 : 90,
                isHearing: string.Equals(deadline.DeadlineType, CourtSyncNormalization.EventTypes.Hearing, StringComparison.OrdinalIgnoreCase)));
        }

        var hearingEvents = clientCase.CourtEvents
            .Where(item => string.Equals(item.EventType, CourtSyncNormalization.EventTypes.Hearing, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.ScheduledAt)
            .ThenBy(item => item.Id)
            .ToList();
        foreach (var courtEvent in hearingEvents)
        {
            var isInactive = courtEvent.HearingOutcome is not null
                || courtEvent.MovementStatus is CourtSyncNormalization.MovementStatuses.Held
                or CourtSyncNormalization.MovementStatuses.Postponed
                or CourtSyncNormalization.MovementStatuses.Cancelled
                or CourtSyncNormalization.MovementStatuses.Completed
                or CourtSyncNormalization.MovementStatuses.Suspended
                or CourtSyncNormalization.MovementStatuses.Archived;
            result.Add(CreateDeadline(
                $"court-event-{courtEvent.Id}",
                string.IsNullOrWhiteSpace(courtEvent.Title) ? "Судебное заседание" : courtEvent.Title.Trim(),
                isInactive ? null : courtEvent.ScheduledAt,
                courtEvent.AllDay,
                courtEvent.AllDay ? 60 : 90,
                isHearing: true,
                courtEvent));
        }

        if (hearingEvents.Count > 0) separateDeadlineTypes.Add("hearing");

        var legacyDeadlines = new CaseDeadline[]
        {
            CreateDeadline("due", "Крайний срок по делу", clientCase.DueDate, true, 60),
            CreateDeadline("claim", "Дата подачи иска", clientCase.ClaimFiledAt, true, 60),
            CreateDeadline("appeal", IsAppealReviewDue(clientCase.CourtDecisionAt, clientCase.AppealDeadlineAt) ? AppealReviewTaskTitle : "Срок апелляции", clientCase.AppealDeadlineAt, true, 60),
            CreateDeadline("cassation", IsCassationReviewDue(appealDecisionAt, clientCase.CassationDeadlineAt) ? CassationReviewTaskTitle : "Срок кассации", clientCase.CassationDeadlineAt, true, 60),
            CreateDeadline("motion", "Срок подачи ходатайства", clientCase.MotionDeadlineAt, true, 60),
            CreateDeadline("objection", "Срок подачи возражений", clientCase.ObjectionDeadlineAt, true, 60),
            CreateDeadline("hearing", "Судебное заседание", clientCase.NextHearingAt, false, 90, isHearing: true),
        };

        result.AddRange(legacyDeadlines.Where(item => !separateDeadlineTypes.Contains(item.Key)));
        result.Add(new CaseDeadline(
            "writ-obtain",
            "Получить исполнительный лист",
            clientCase.ExecutiveWritReceivedAt.HasValue
                ? null
                : clientCase.CourtDecisionEffectiveAt?.Date.AddDays(7),
            true,
            60,
            "control",
            "исполнительный-лист",
            null,
            null));
        result.Add(new CaseDeadline(
            "writ-submit",
            "Подать исполнительный лист в ФССП или банк",
            clientCase.ExecutiveWritSubmittedAt.HasValue ? null : clientCase.ExecutiveWritReceivedAt?.Date.AddDays(5),
            true,
            60,
            "control",
            "исполнительный-лист",
            null,
            null));
        var appealHearings = clientCase.CourtEvents
            .Where(item => item.CourtInstance == "appeal"
                && item.EventType == CourtSyncNormalization.EventTypes.Hearing
                && item.MovementStatus is not CourtSyncNormalization.MovementStatuses.Held
                    and not CourtSyncNormalization.MovementStatuses.Postponed
                    and not CourtSyncNormalization.MovementStatuses.Cancelled
                    and not CourtSyncNormalization.MovementStatuses.Completed
                    and not CourtSyncNormalization.MovementStatuses.Archived)
            .OrderBy(item => item.ScheduledAt)
            .ThenBy(item => item.Id)
            .ToArray();
        foreach (var complaint in clientCase.CourtComplaints.Where(item => item.ComplaintType == "appealComplaint" && item.TargetInstance == "appeal"))
        {
            var filed = !complaint.IsArchived
                && complaint.FiledAt.HasValue
                && complaint.Status is ("filed" or "accepted");
            var ourFiledAppeal = filed && complaint.FiledBy == "ours";
            var opponentFiledAppeal = filed && complaint.FiledBy == "opponent";
            var hearing = appealHearings.FirstOrDefault(item => !complaint.FiledAt.HasValue || item.ScheduledAt.Date >= complaint.FiledAt.Value.Date);
            result.Add(new CaseDeadline(
                $"appeal-followup-{complaint.Id}",
                AppealHearingFollowupTaskTitle,
                ourFiledAppeal && hearing is null ? complaint.FiledAt!.Value.Date.AddDays(10) : null,
                true, 60, "control", "апелляция", null, null,
                ourFiledAppeal && hearing is not null, complaint.FiledAt));
            result.Add(new CaseDeadline(
                $"appeal-objection-{complaint.Id}",
                AppealObjectionTaskTitle,
                opponentFiledAppeal ? hearing?.ScheduledAt : null,
                hearing?.AllDay ?? false, 90, "control", "апелляция", hearing, hearing?.ScheduledAt));
        }
        return result;
    }

    private static bool IsAppealReviewDue(DateTime? decisionAt, DateTime? deadlineAt)
        => decisionAt.HasValue && deadlineAt.HasValue && deadlineAt.Value.Date >= decisionAt.Value.Date;

    private static DateTime? FindAppealDecisionAt(ClientCase clientCase)
        => clientCase.CourtEvents
            .Where(item => item.EventType == CourtSyncNormalization.EventTypes.Decision
                && item.CourtInstance == "appeal"
                && item.MovementStatus is not CourtSyncNormalization.MovementStatuses.Cancelled
                    and not CourtSyncNormalization.MovementStatuses.Archived
                && item.ScheduledAt.Date <= DateTime.UtcNow.Date)
            .Select(item => (DateTime?)item.ScheduledAt)
            .OrderByDescending(item => item)
            .FirstOrDefault();

    private static bool IsCassationReviewDue(DateTime? appealDecisionAt, DateTime? deadlineAt)
        => appealDecisionAt.HasValue && deadlineAt.HasValue && deadlineAt.Value.Date >= appealDecisionAt.Value.Date;

    private static string ResolveReviewTaskTitle(ClientCase clientCase, ClientCaseProceduralDeadline deadline, DateTime? appealDecisionAt)
    {
        var defaultTitle = ResolveDeadlineTitle(deadline.DeadlineType);
        if (!string.IsNullOrWhiteSpace(deadline.Title) && !string.Equals(deadline.Title.Trim(), defaultTitle, StringComparison.OrdinalIgnoreCase))
        {
            return deadline.Title;
        }
        if (string.Equals(deadline.DeadlineType, "appeal", StringComparison.OrdinalIgnoreCase)
            && IsAppealReviewDue(clientCase.CourtDecisionAt, deadline.DueAt))
        {
            return AppealReviewTaskTitle;
        }
        if (string.Equals(deadline.DeadlineType, "cassation", StringComparison.OrdinalIgnoreCase)
            && IsCassationReviewDue(appealDecisionAt, deadline.DueAt))
        {
            return CassationReviewTaskTitle;
        }
        return string.IsNullOrWhiteSpace(deadline.Title) ? defaultTitle : deadline.Title;
    }

    private static CaseDeadline CreateDeadline(
        string key,
        string title,
        DateTime? dueAt,
        bool dateOnly,
        int durationMinutes,
        bool isHearing = false,
        ClientCaseCourtEvent? courtEvent = null)
        => new(
            key,
            isHearing ? "Подготовиться к заседанию" : title,
            isHearing ? dueAt?.AddDays(-3) : dueAt,
            dateOnly,
            durationMinutes,
            isHearing ? "courtHearing" : "control",
            isHearing ? "судебное-заседание" : "процессуальный-срок",
            courtEvent,
            isHearing ? dueAt : null);

    private static string ResolveDeadlineTitle(string deadlineType)
        => deadlineType.ToLowerInvariant() switch
        {
            "appeal" => "Срок апелляции",
            "cassation" => "Срок кассации",
            "motion" => "Срок подачи ходатайства",
            "objection" => "Срок подачи возражений",
            "claim" => "Дата подачи иска",
            "hearing" => "Судебное заседание",
            _ => "Процессуальный срок",
        };

    private static string BuildDescription(ClientCase clientCase, CaseDeadline deadline)
    {
        var parts = new List<string>
        {
            deadline.TaskType == "courtHearing"
                ? $"Судебное заседание по делу «{clientCase.Title}» назначено судом."
                : $"Автоконтроль процессуального срока по делу «{clientCase.Title}».",
        };

        if (!string.IsNullOrWhiteSpace(clientCase.CaseNumber))
        {
            parts.Add($"Номер дела: {clientCase.CaseNumber.Trim()}.");
        }

        if (!string.IsNullOrWhiteSpace(clientCase.Stage))
        {
            parts.Add($"Этап: {clientCase.Stage.Trim()}.");
        }

        if (!string.IsNullOrWhiteSpace(clientCase.CourtName))
        {
            parts.Add($"Суд: {clientCase.CourtName.Trim()}.");
        }

        if (deadline.Title == AppealReviewTaskTitle && clientCase.CourtDecisionAt.HasValue)
        {
            parts.Add($"Решение вынесено {clientCase.CourtDecisionAt.Value:dd.MM.yyyy}. Оцените необходимость подачи жалобы до истечения срока обжалования.");
        }
        if (deadline.Title == CassationReviewTaskTitle && FindAppealDecisionAt(clientCase) is { } appealDecisionAt)
        {
            parts.Add($"Решение апелляции вынесено {appealDecisionAt:dd.MM.yyyy}. Оцените необходимость кассации и подготовьте жалобу до истечения срока кассации.");
        }
        if (deadline.Key.StartsWith("appeal-followup-", StringComparison.Ordinal) && deadline.ReferenceDate.HasValue)
        {
            parts.Add($"Жалоба нашей стороны подана {deadline.ReferenceDate.Value:dd.MM.yyyy}. Проверьте, назначено ли заседание апелляционной инстанции. Дата задачи — внутренний ориентир через 10 дней после подачи.");
        }
        if (deadline.Key.StartsWith("appeal-objection-", StringComparison.Ordinal))
        {
            parts.Add("Апелляционная жалоба подана против нашей стороны. Подготовьте возражения до заседания апелляционной инстанции.");
        }
        if (deadline.Key == "writ-obtain" && clientCase.CourtDecisionEffectiveAt.HasValue)
        {
            parts.Add($"Решение вступило в силу {clientCase.CourtDecisionEffectiveAt.Value:dd.MM.yyyy}. Получите исполнительный лист. Дата задачи — внутренний ориентир через 7 дней, не процессуальный срок.");
        }
        if (deadline.Key == "writ-submit" && clientCase.ExecutiveWritReceivedAt.HasValue)
        {
            parts.Add($"Исполнительный лист получен {clientCase.ExecutiveWritReceivedAt.Value:dd.MM.yyyy}. Подайте его в ФССП или банк. Дата задачи — внутренний ориентир через 5 дней, не процессуальный срок.");
        }

        if (deadline.CourtEvent is { } courtEvent)
        {
            if (!string.IsNullOrWhiteSpace(courtEvent.Title))
            {
                parts.Add($"Событие: {courtEvent.Title.Trim()}.");
            }
            if (!string.IsNullOrWhiteSpace(courtEvent.CourtCaseNumber))
            {
                parts.Add($"Номер в суде: {courtEvent.CourtCaseNumber.Trim()}.");
            }

            if (!string.IsNullOrWhiteSpace(courtEvent.Courtroom))
            {
                parts.Add($"Зал или место: {courtEvent.Courtroom.Trim()}.");
            }

            if (!string.IsNullOrWhiteSpace(courtEvent.JudgeName))
            {
                parts.Add($"Судья: {courtEvent.JudgeName.Trim()}.");
            }
        }

        if (deadline.HearingAt.HasValue)
        {
            parts.Add($"Заседание назначено на {deadline.HearingAt.Value:dd.MM.yyyy HH:mm}.");
        }

        parts.Add($"Контроль: {deadline.Title}.");
        return string.Join(Environment.NewLine, parts);
    }

    private static DateTime NormalizeDueAt(DateTime value, bool dateOnly)
    {
        var normalized = value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();

        return dateOnly ? normalized.Date.AddHours(9) : normalized;
    }

    private static string ResolvePriority(DateTime dueAt, DateTime now)
    {
        if (dueAt <= now.AddDays(1)) return "urgent";
        if (dueAt <= now.AddDays(3)) return "high";
        return "normal";
    }

    private static async Task<User?> ResolveAssigneeAsync(PaymentContext db, ClientCase clientCase, int? actorUserId, CancellationToken ct)
    {
        var userId = clientCase.ResponsibleUserId
            ?? clientCase.Client?.ResponsibleUserId
            ?? clientCase.LegalEntity?.ResponsibleUserId
            ?? actorUserId;

        if (!userId.HasValue)
        {
            return null;
        }

        return await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(user => user.Id == userId.Value && user.IsActive && user.IsApproved, ct);
    }

    private static CaseOwner? ResolveOwner(ClientCase clientCase)
    {
        if (clientCase.ClientId.HasValue && clientCase.Client is not null)
        {
            return new CaseOwner(clientCase.ClientId.Value, null, clientCase.Client.Name);
        }

        if (clientCase.LegalEntityId.HasValue && clientCase.LegalEntity is not null)
        {
            var name = !string.IsNullOrWhiteSpace(clientCase.LegalEntity.ShortName)
                ? clientCase.LegalEntity.ShortName.Trim()
                : !string.IsNullOrWhiteSpace(clientCase.LegalEntity.FullName)
                    ? clientCase.LegalEntity.FullName.Trim()
                    : $"Юрлицо #{clientCase.LegalEntity.Id}";
            return new CaseOwner(null, clientCase.LegalEntityId.Value, name);
        }

        return null;
    }

    private static void UpsertPrimaryAssignee(TaskJournalItem task, int? userId, string displayName, string email, DateTime now)
    {
        var participant = task.Participants
            .Where(item => item.Role == "assignee")
            .OrderBy(item => item.SortOrder)
            .FirstOrDefault();

        if (participant is null)
        {
            task.Participants.Add(new TaskJournalParticipant
            {
                UserId = userId,
                Role = "assignee",
                DisplayName = displayName,
                Email = email,
                SortOrder = 0,
                ApprovalStatus = "pending",
                CreatedAt = now,
                UpdatedAt = now,
            });
            return;
        }

        participant.UserId = userId;
        participant.DisplayName = displayName;
        participant.Email = email;
        participant.SortOrder = 0;
        participant.UpdatedAt = now;
    }

    private static void AddHistory(PaymentContext db, TaskJournalItem task, int? actorUserId, string actorName, string text, DateTime now)
        => db.TaskJournalComments.Add(CreateHistory(task.Id, actorUserId, actorName, text, now));

    private static TaskJournalComment CreateHistory(int taskId, int? actorUserId, string actorName, string text, DateTime now)
        => new()
        {
            TaskJournalItemId = taskId,
            AuthorUserId = actorUserId,
            AuthorName = actorName,
            Kind = "system",
            Text = text,
            MetadataJson = "{}",
            CreatedAt = now,
            UpdatedAt = now,
        };

    private static bool HasTag(TaskJournalItem task, string tag)
        => DeserializeList(task.TagsJson).Contains(tag, StringComparer.OrdinalIgnoreCase);

    private static string BuildDeadlineTag(string key) => $"{DeadlineTag}:{key}";

    private static string BuildObjectName(string ownerName, string? caseTitle)
        => string.IsNullOrWhiteSpace(caseTitle) ? ownerName.Trim() : $"{ownerName.Trim()} - {caseTitle.Trim()}";

    private static string DisplayName(User user)
        => string.IsNullOrWhiteSpace(user.FullName) ? user.Email : user.FullName.Trim();

    private static string NormalizeActorName(string actorName)
        => string.IsNullOrWhiteSpace(actorName) ? "Система" : actorName.Trim();

    private static bool IsFinishedCaseStatus(ClientCaseStatus status)
        => status is ClientCaseStatus.Closed or ClientCaseStatus.Lost;

    private static string SerializeList(IEnumerable<string> values)
        => JsonSerializer.Serialize(values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), JsonOptions);

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
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private static bool SetIfChanged(TaskJournalItem task, Action<TaskJournalItem, string> setter, string current, string value)
    {
        if (string.Equals(current, value, StringComparison.Ordinal))
        {
            return false;
        }

        setter(task, value);
        return true;
    }

    private readonly record struct CaseDeadline(
        string Key,
        string Title,
        DateTime? DueAt,
        bool DateOnly,
        int DurationMinutes,
        string TaskType,
        string CategoryTag,
        ClientCaseCourtEvent? CourtEvent,
        DateTime? HearingAt,
        bool CompleteOnHearing = false,
        DateTime? ReferenceDate = null);
    private readonly record struct CaseOwner(int? ClientId, int? LegalEntityId, string Name);
}
