using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public static class ClientCaseTaskSyncService
{
    private static readonly HashSet<string> ClosedTaskStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "done",
        "cancelled",
    };

    public static Task<int> CloseActiveTasksForClosedCaseAsync(
        PaymentContext db,
        int clientCaseId,
        int? actorUserId,
        string? actorName,
        CancellationToken ct)
        => CloseActiveTasksForClosedCasesAsync(db, [clientCaseId], actorUserId, actorName, ct);

    public static async Task<int> CloseActiveTasksForClosedCasesAsync(
        PaymentContext db,
        IReadOnlyCollection<int> clientCaseIds,
        int? actorUserId,
        string? actorName,
        CancellationToken ct)
    {
        var ids = clientCaseIds
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
        if (ids.Length == 0)
        {
            return 0;
        }

        var now = DateTime.UtcNow;
        var normalizedActorName = string.IsNullOrWhiteSpace(actorName)
            ? "Система"
            : actorName.Trim();

        var tasks = await db.TaskJournalItems
            .Where(task => task.ClientCaseId.HasValue
                && ids.Contains(task.ClientCaseId.Value)
                && task.Status != "done"
                && task.Status != "cancelled"
                && !task.IsCaseFinalReport)
            .ToListAsync(ct);

        foreach (var task in tasks)
        {
            if (ClosedTaskStatuses.Contains(task.Status))
            {
                continue;
            }

            task.Status = "done";
            task.UpdatedAt = now;
            if (string.IsNullOrWhiteSpace(task.Result))
            {
                task.Result = "Задача закрыта автоматически, потому что связанное дело закрыто.";
            }

            db.TaskJournalComments.Add(new TaskJournalComment
            {
                TaskJournalItemId = task.Id,
                AuthorUserId = actorUserId,
                AuthorName = normalizedActorName,
                Kind = "history",
                Text = "Связанное дело закрыто. Задача автоматически переведена в статус «Готово».",
                MetadataJson = "{}",
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        return tasks.Count;
    }
}
