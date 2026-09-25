using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public static class ClientCaseFinalReportTaskService
{
    public const string AssigneeUserIdSettingKey = "tasks.automation.caseFinalReport.assigneeUserId";
    public const string TaskTag = "case-final-report";
    private const string TaskTitle = "Закрыть дело, сформировать отчёт";

    public static async Task<HashSet<int>> FilterCourtCaseIdsAsync(
        PaymentContext db,
        IReadOnlyCollection<ClientCase> cases,
        CancellationToken ct)
    {
        var result = cases.Where(HasCourtFields).Select(item => item.Id).ToHashSet();
        var remainingIds = cases.Select(item => item.Id).Where(id => id > 0 && !result.Contains(id)).ToArray();
        if (remainingIds.Length == 0) return result;

        var relatedIds = await db.ClientCases.AsNoTracking()
            .Where(item => remainingIds.Contains(item.Id)
                && (item.CourtInstances.Any()
                    || item.CourtEvents.Any()
                    || item.CourtParties.Any(party => !party.IsArchived)))
            .Select(item => item.Id)
            .ToListAsync(ct);
        result.UnionWith(relatedIds);
        return result;
    }

    public static bool HasCourtFields(ClientCase item)
        => !string.IsNullOrWhiteSpace(item.CourtName)
            || !string.IsNullOrWhiteSpace(item.OurCourtRole)
            || !string.IsNullOrWhiteSpace(item.CourtCaseNumber)
            || !string.IsNullOrWhiteSpace(item.JudicialUid)
            || !string.IsNullOrWhiteSpace(item.CourtUrl)
            || item.NextHearingAt.HasValue;

    public static async Task<User?> ResolveAssigneeAsync(PaymentContext db, CancellationToken ct)
    {
        var raw = await db.SystemSettings.AsNoTracking()
            .Where(setting => setting.Key == AssigneeUserIdSettingKey)
            .Select(setting => setting.Value)
            .FirstOrDefaultAsync(ct);
        if (!int.TryParse(raw, out var userId) || userId <= 0)
        {
            return null;
        }

        var assignee = await db.Users.AsNoTracking()
            .Include(user => user.Role)
            .FirstOrDefaultAsync(user => user.Id == userId && user.IsActive && user.IsApproved, ct);
        if (assignee is null)
        {
            return null;
        }

        if (string.Equals(assignee.Role?.Name, "admin", StringComparison.OrdinalIgnoreCase))
        {
            return assignee;
        }

        var permission = await db.GetUserSectionPermissionAsync(userId, RolePermissionCatalog.Cases, ct);
        return permission is { CanView: true }
            && DataVisibilityScope.Normalize(permission.ViewScope, DataVisibilityScope.Own) == DataVisibilityScope.All
            ? assignee
            : null;
    }

    public static async Task<int> CreateForClosedCasesAsync(
        PaymentContext db,
        IReadOnlyCollection<int> clientCaseIds,
        User assignee,
        int? actorUserId,
        string? actorName,
        CancellationToken ct)
    {
        var ids = clientCaseIds.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0) return 0;

        var existingCaseIds = await db.TaskJournalItems.AsNoTracking()
            .Where(task => task.ClientCaseId.HasValue && ids.Contains(task.ClientCaseId.Value)
                && task.IsCaseFinalReport && task.Status != "cancelled" && task.Status != "done")
            .Select(task => task.ClientCaseId!.Value)
            .Distinct()
            .ToListAsync(ct);
        var existing = existingCaseIds.ToHashSet();
        var cases = await db.ClientCases
            .Include(item => item.Client)
            .Include(item => item.LegalEntity)
            .Where(item => ids.Contains(item.Id))
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        var assigneeName = string.IsNullOrWhiteSpace(assignee.FullName) ? assignee.Email : assignee.FullName.Trim();
        var authorName = string.IsNullOrWhiteSpace(actorName) ? "Система" : actorName.Trim();
        var created = 0;
        foreach (var clientCase in cases)
        {
            if (!existing.Add(clientCase.Id)) continue;
            var ownerName = clientCase.Client?.Name
                ?? clientCase.LegalEntity?.ShortName
                ?? clientCase.LegalEntity?.FullName
                ?? $"Дело #{clientCase.Id}";
            var objectName = $"{ownerName.Trim()} - {clientCase.Title.Trim()}";
            var task = new TaskJournalItem
            {
                ClientCaseId = clientCase.Id,
                ClientId = clientCase.ClientId,
                LegalEntityId = clientCase.LegalEntityId,
                ObjectName = objectName.Length > 300 ? objectName[..300] : objectName,
                Title = TaskTitle,
                Description = "Проверьте завершение работы по делу, подготовьте итоговый отчёт и приложите подтверждающие документы. Срок через 10 дней после закрытия дела — внутренний рабочий ориентир.",
                AssigneeUserId = assignee.Id,
                AssigneeName = assigneeName,
                AuthorUserId = actorUserId ?? assignee.Id,
                AuthorName = authorName,
                DueAt = now.AddDays(10),
                PlannedDurationMinutes = 60,
                Type = "task",
                Priority = "normal",
                Status = "new",
                TagsJson = JsonSerializer.Serialize(new[] { TaskTag }),
                IsCaseFinalReport = true,
                RemindersJson = JsonSerializer.Serialize(new[] { "before3d", "before1d", "atDue", "dailyOverdue" }),
                ChannelsJson = JsonSerializer.Serialize(new[] { "inApp", "email" }),
                CreatedAt = now,
                UpdatedAt = now,
            };
            task.Participants.Add(new TaskJournalParticipant
            {
                UserId = assignee.Id,
                Role = "assignee",
                DisplayName = assigneeName,
                Email = assignee.Email,
                SortOrder = 0,
                ApprovalStatus = "pending",
                CreatedAt = now,
                UpdatedAt = now,
            });
            task.Comments.Add(new TaskJournalComment
            {
                AuthorUserId = actorUserId,
                AuthorName = authorName,
                Kind = "system",
                Text = "Задача на итоговый отчёт создана после закрытия дела.",
                MetadataJson = "{}",
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.TaskJournalItems.Add(task);
            created++;
        }

        return created;
    }

    public static async Task<int> CancelForReopenedCasesAsync(
        PaymentContext db,
        IReadOnlyCollection<int> clientCaseIds,
        int? actorUserId,
        string? actorName,
        CancellationToken ct)
    {
        var ids = clientCaseIds.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0) return 0;

        var tasks = await db.TaskJournalItems
            .Where(task => task.ClientCaseId.HasValue && ids.Contains(task.ClientCaseId.Value)
                && task.IsCaseFinalReport && task.Status != "cancelled" && task.Status != "done")
            .ToListAsync(ct);
        var now = DateTime.UtcNow;
        foreach (var task in tasks)
        {
            task.Status = "cancelled";
            task.Result = "Дело снова открыто; итоговый отчёт потребуется после нового закрытия.";
            task.UpdatedAt = now;
            db.TaskJournalComments.Add(new TaskJournalComment
            {
                TaskJournalItemId = task.Id,
                AuthorUserId = actorUserId,
                AuthorName = string.IsNullOrWhiteSpace(actorName) ? "Система" : actorName.Trim(),
                Kind = "history",
                Text = "Дело открыто повторно. Задача итогового отчёта отменена.",
                MetadataJson = "{}",
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        return tasks.Count;
    }
}
