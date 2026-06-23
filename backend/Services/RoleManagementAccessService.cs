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
        var userId = user.GetUserId();
        if (!userId.HasValue)
        {
            return RoleManagementAccessSnapshot.None;
        }

        var userRole = await _db.Users
            .AsNoTracking()
            .Where(entity => entity.Id == userId.Value && entity.IsActive)
            .Select(entity => new
            {
                entity.RoleId,
                RoleName = entity.Role != null ? entity.Role.Name : string.Empty,
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (userRole is null)
        {
            return new RoleManagementAccessSnapshot(userId, null, false, false, false, false, false);
        }

        if (string.Equals(userRole.RoleName, "admin", StringComparison.OrdinalIgnoreCase))
        {
            return new RoleManagementAccessSnapshot(
                userId,
                userRole.RoleId,
                true,
                true,
                true,
                true,
                true);
        }

        var permissions = await _db.RolePermissions
            .AsNoTracking()
            .Where(entity => entity.RoleId == userRole.RoleId && entity.Section == RolePermissionCatalog.Roles)
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
            userRole.RoleId,
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
