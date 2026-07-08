using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public static class ClientCaseDeadlineTaskSyncService
{
    private const string DeadlineTag = "case-deadline";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> ClosedTaskStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "done", "cancelled"
    };

    public static async Task SyncAsync(
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
            .AsSplitQuery()
            .FirstOrDefaultAsync(item => item.Id == clientCaseId, ct);
        if (clientCase is null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var normalizedActorName = NormalizeActorName(actorName);
        var existingTasks = await db.TaskJournalItems
            .Include(item => item.Participants)
            .Where(item => item.ClientCaseId == clientCase.Id && item.TagsJson.Contains(DeadlineTag))
            .ToListAsync(ct);

        if (IsFinishedCaseStatus(clientCase.Status))
        {
            await ClientCaseTaskSyncService.CloseActiveTasksForClosedCaseAsync(db, clientCase.Id, actorUserId, normalizedActorName, ct);
            await db.SaveChangesAsync(ct);
            return;
        }

        var owner = ResolveOwner(clientCase);
        if (owner is null)
        {
            return;
        }

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
                    activeTask.Status = "cancelled";
                    activeTask.Result = "Срок удален из карточки дела.";
                    activeTask.UpdatedAt = now;
                    AddHistory(db, activeTask, actorUserId, normalizedActorName, "Срок удален из карточки дела, контрольная задача отменена.", now);
                }

                continue;
            }

            var dueAt = NormalizeDueAt(deadline.DueAt.Value, deadline.DateOnly);
            if (activeTask is null || ClosedTaskStatuses.Contains(activeTask.Status))
            {
                var task = BuildTask(
                    clientCase,
                    owner.Value,
                    deadline,
                    dueAt,
                    assigneeUserId,
                    assigneeName,
                    assigneeEmail,
                    actorUserId,
                    normalizedActorName,
                    now);
                db.TaskJournalItems.Add(task);
                existingTasks.Add(task);
                continue;
            }

            var changed = false;
            changed |= SetIfChanged(activeTask, static (task, value) => task.ObjectName = value, activeTask.ObjectName, BuildObjectName(owner.Value.Name, clientCase.Title));
            changed |= SetIfChanged(activeTask, static (task, value) => task.Title = value, activeTask.Title, deadline.Title);
            changed |= SetIfChanged(activeTask, static (task, value) => task.Description = value, activeTask.Description ?? string.Empty, BuildDescription(clientCase, deadline));
            changed |= SetIfChanged(activeTask, static (task, value) => task.Type = value, activeTask.Type, "control");
            changed |= SetIfChanged(activeTask, static (task, value) => task.Priority = value, activeTask.Priority, ResolvePriority(dueAt, now));
            changed |= SetIfChanged(activeTask, static (task, value) => task.Status = value, activeTask.Status, activeTask.Status == "cancelled" ? "new" : activeTask.Status);
            changed |= SetIfChanged(activeTask, static (task, value) => task.RemindersJson = value, activeTask.RemindersJson, SerializeList(["before7d", "before3d", "before1d", "atDue", "dailyOverdue"]));
            changed |= SetIfChanged(activeTask, static (task, value) => task.ChannelsJson = value, activeTask.ChannelsJson, SerializeList(["inApp", "email"]));
            changed |= SetIfChanged(activeTask, static (task, value) => task.TagsJson = value, activeTask.TagsJson, SerializeList([DeadlineTag, tag, "процессуальный-срок"]));

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
                AddHistory(db, activeTask, actorUserId, normalizedActorName, "Контрольная задача синхронизирована со сроками карточки дела.", now);
            }
        }

        await db.SaveChangesAsync(ct);
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
            Type = "control",
            Priority = ResolvePriority(dueAt, now),
            Status = "new",
            Result = null,
            RemindersJson = SerializeList(["before7d", "before3d", "before1d", "atDue", "dailyOverdue"]),
            ChannelsJson = SerializeList(["inApp", "email"]),
            TagsJson = SerializeList([DeadlineTag, tag, "процессуальный-срок"]),
            CreatedAt = now,
            UpdatedAt = now,
        };

        UpsertPrimaryAssignee(task, assigneeUserId, assigneeName, assigneeEmail, now);
        task.Comments.Add(CreateHistory(task.Id, actorUserId, actorName, "Контрольная задача создана из процессуального срока карточки дела.", now));
        return task;
    }

    private static IReadOnlyList<CaseDeadline> BuildDeadlines(ClientCase clientCase)
    {
        if (ClientCaseStageClassifier.IsPretrial(clientCase))
        {
            return
            [
                new("due", "Крайний срок по делу", clientCase.DueDate, true, 60)
            ];
        }

        var result = new List<CaseDeadline>();
        var separateDeadlineTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var deadline in clientCase.ProceduralDeadlines.OrderBy(item => item.DueAt).ThenBy(item => item.Id))
        {
            separateDeadlineTypes.Add(deadline.DeadlineType);
            var isClosed = string.Equals(deadline.Status, "closed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(deadline.Status, "cancelled", StringComparison.OrdinalIgnoreCase);
            result.Add(new CaseDeadline(
                $"entity-{deadline.Id}",
                string.IsNullOrWhiteSpace(deadline.Title) ? ResolveDeadlineTitle(deadline.DeadlineType) : deadline.Title,
                isClosed ? null : deadline.DueAt,
                deadline.AllDay,
                deadline.AllDay ? 60 : 90));
        }

        var legacyDeadlines = new CaseDeadline[]
        {
            new("due", "Крайний срок по делу", clientCase.DueDate, true, 60),
            new("claim", "Дата подачи иска", clientCase.ClaimFiledAt, true, 60),
            new("appeal", "Срок апелляции", clientCase.AppealDeadlineAt, true, 60),
            new("cassation", "Срок кассации", clientCase.CassationDeadlineAt, true, 60),
            new("motion", "Срок подачи ходатайства", clientCase.MotionDeadlineAt, true, 60),
            new("objection", "Срок подачи возражений", clientCase.ObjectionDeadlineAt, true, 60),
            new("hearing", "Судебное заседание", clientCase.NextHearingAt, false, 90),
        };

        result.AddRange(legacyDeadlines.Where(item => !separateDeadlineTypes.Contains(item.Key)));
        return result;
    }

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
            $"Автоконтроль процессуального срока по делу «{clientCase.Title}».",
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

    private readonly record struct CaseDeadline(string Key, string Title, DateTime? DueAt, bool DateOnly, int DurationMinutes);
    private readonly record struct CaseOwner(int? ClientId, int? LegalEntityId, string Name);
}
