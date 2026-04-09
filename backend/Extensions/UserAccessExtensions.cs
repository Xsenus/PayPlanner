using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Extensions;

public static class UserAccessExtensions
{
    public static Task<bool> IsUserAdminAsync(this PaymentContext db, int userId, CancellationToken ct = default)
        => db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId && u.IsActive)
            .Select(u => u.Role != null && u.Role.Name == "admin")
            .FirstOrDefaultAsync(ct);

    public static Task<RolePermission?> GetUserSectionPermissionAsync(
        this PaymentContext db,
        int userId,
        string section,
        CancellationToken ct = default)
        => db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId && u.IsActive)
            .SelectMany(
                u => db.RolePermissions
                    .Where(permission => permission.RoleId == u.RoleId && permission.Section == section))
            .FirstOrDefaultAsync(ct);
}
