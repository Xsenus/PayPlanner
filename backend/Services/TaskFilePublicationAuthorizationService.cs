using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public interface ITaskFilePublicationAuthorizationService
{
    Task<bool> CanPublishAsync(int actorId, int taskId, CancellationToken ct);
}

/// <summary>
/// Authorizes durable upload publication using current database state, not the
/// initiating HTTP request, stored role claims, or the upload intent itself.
/// </summary>
public sealed class TaskFilePublicationAuthorizationService(PaymentContext db) : ITaskFilePublicationAuthorizationService
{
    public async Task<bool> CanPublishAsync(int actorId, int taskId, CancellationToken ct)
    {
        if (actorId <= 0 || taskId <= 0) return false;
        // Only identity is supplied. ResolveSectionAccessAsync reads the live role
        // and permissions; no admin claim or null-principal fallback is possible.
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, actorId.ToString(System.Globalization.CultureInfo.InvariantCulture))], "storage-publication"));
        var access = await db.ResolveSectionAccessAsync(principal, RolePermissionCatalog.Tasks, ct,
            requireApprovedUser: true);
        if (access is null || (!access.Visibility.IsAdmin && access.Permission?.CanView != true)) return false;
        var task = await db.TaskJournalItems.AsNoTracking().ApplyTaskVisibility(access.Visibility)
            .Where(t => t.Id == taskId)
            .Select(t => new
            {
                IsAuthor = t.AuthorUserId == actorId,
                IsExecutor = t.AssigneeUserId == actorId,
                HasParticipant = t.Participants.Any(p => p.UserId == actorId && p.Role.ToLower() != "observer")
            }).SingleOrDefaultAsync(ct);
        return task is not null && TaskCollaborationPermission.CanContribute(access.Visibility,
            access.Permission?.CanEdit == true, task.IsAuthor, task.IsExecutor, task.HasParticipant);
    }
}

public static class TaskCollaborationPermission
{
    // Shared with the HTTP card. Work on attachments/comments is independent of
    // permission to edit the task's fields or to finish it without approval.
    public static bool CanContribute(DataVisibilityAccess visibility, bool canEdit,
        bool isAuthor, bool isExecutor, bool hasWorkingParticipant)
        => visibility.IsAdmin
            || (canEdit && (visibility.SeesAll
                || string.Equals(visibility.ViewScope, DataVisibilityScope.Department, StringComparison.OrdinalIgnoreCase)))
            || isAuthor || isExecutor || hasWorkingParticipant;
}
