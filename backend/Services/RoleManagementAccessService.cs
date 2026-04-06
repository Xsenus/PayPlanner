using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public sealed class RoleManagementAccessService
{
    private readonly PaymentContext _db;

    public RoleManagementAccessService(PaymentContext db)
    {
        _db = db;
    }

    public async Task<RoleManagementAccessSnapshot> GetSnapshotAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        if (user.IsInRole("admin"))
        {
            return new RoleManagementAccessSnapshot(
                user.GetUserId(),
                null,
                true,
                true,
                true,
                true,
                true);
        }

        var userId = user.GetUserId();
        if (!userId.HasValue)
        {
            return RoleManagementAccessSnapshot.None;
        }

        var userRoleId = await _db.Users
            .AsNoTracking()
            .Where(entity => entity.Id == userId.Value)
            .Select(entity => (int?)entity.RoleId)
            .FirstOrDefaultAsync(cancellationToken);

        if (!userRoleId.HasValue)
        {
            return new RoleManagementAccessSnapshot(userId, null, false, false, false, false, false);
        }

        var permissions = await _db.RolePermissions
            .AsNoTracking()
            .Where(entity => entity.RoleId == userRoleId.Value && entity.Section == RolePermissionCatalog.Roles)
            .Select(entity => new
            {
                entity.CanView,
                entity.CanCreate,
                entity.CanEdit,
                entity.CanDelete
            })
            .FirstOrDefaultAsync(cancellationToken);

        return new RoleManagementAccessSnapshot(
            userId,
            userRoleId,
            false,
            permissions?.CanView ?? false,
            permissions?.CanCreate ?? false,
            permissions?.CanEdit ?? false,
            permissions?.CanDelete ?? false);
    }
}

public sealed record RoleManagementAccessSnapshot(
    int? UserId,
    int? RoleId,
    bool IsAdmin,
    bool CanView,
    bool CanCreate,
    bool CanEdit,
    bool CanDelete)
{
    public static RoleManagementAccessSnapshot None { get; } =
        new(null, null, false, false, false, false, false);
}
