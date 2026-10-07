using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public static class CaseNotificationRecipients
{
    public static async Task<IReadOnlyCollection<int>> ResolveAsync(PaymentContext db, int clientCaseId, CancellationToken ct)
    {
        var caseAccess = await db.ClientCases
            .AsNoTracking()
            .Where(clientCase => clientCase.Id == clientCaseId)
            .Select(clientCase => new
            {
                clientCase.ResponsibleUserId,
                ClientResponsibleUserId = clientCase.Client != null ? clientCase.Client.ResponsibleUserId : null,
                LegalEntityResponsibleUserId = clientCase.LegalEntity != null ? clientCase.LegalEntity.ResponsibleUserId : null,
                clientCase.IsHiddenFromExecutor,
            })
            .SingleOrDefaultAsync(ct);
        if (caseAccess is null)
        {
            return Array.Empty<int>();
        }

        var responsibleUserIds = new[]
            {
                caseAccess.ResponsibleUserId,
                caseAccess.ClientResponsibleUserId,
                caseAccess.LegalEntityResponsibleUserId,
            }
            .Where(userId => userId.HasValue && userId.Value > 0)
            .Select(userId => userId!.Value)
            .Distinct()
            .ToArray();
        if (responsibleUserIds.Length == 0)
        {
            return Array.Empty<int>();
        }

        var allowedUserIds = await db.Users
            .AsNoTracking()
            .Where(user => responsibleUserIds.Contains(user.Id)
                && user.IsActive
                && user.IsApproved
                && user.Role != null
                && (user.Role.Name == "admin"
                    || user.Role.Permissions.Any(permission =>
                        permission.Section == RolePermissionCatalog.Cases
                        && permission.CanView)))
            .Select(user => user.Id)
            .ToListAsync(ct);

        var visibilityByUserId = await db.ResolveDataVisibilitiesForUsersAsync(
            allowedUserIds,
            RolePermissionCatalog.Cases,
            ct);
        var result = new List<int>(allowedUserIds.Count);
        foreach (var userId in allowedUserIds)
        {
            if (!visibilityByUserId.TryGetValue(userId, out var visibility) || visibility is null)
            {
                continue;
            }

            var canOpenCase = visibility.SeesAll
                || (!caseAccess.IsHiddenFromExecutor && responsibleUserIds.Any(visibility.CanSeeUser));
            if (canOpenCase)
            {
                result.Add(userId);
            }
        }

        return result;
    }
}
