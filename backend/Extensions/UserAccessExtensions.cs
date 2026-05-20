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

    public static async Task<RolePermission?> GetUserSectionPermissionAsync(
        this PaymentContext db,
        int userId,
        string section,
        CancellationToken ct = default)
    {
        var permission = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId && u.IsActive)
            .SelectMany(
                u => db.RolePermissions
                    .Where(permission => permission.RoleId == u.RoleId && permission.Section == section))
            .FirstOrDefaultAsync(ct);

        if (permission is not null)
        {
            return permission;
        }

        var fallbackSection = RolePermissionCatalog.GetFallbackSection(section);
        if (string.IsNullOrWhiteSpace(fallbackSection))
        {
            return null;
        }

        return await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId && u.IsActive)
            .SelectMany(
                u => db.RolePermissions
                    .Where(item => item.RoleId == u.RoleId && item.Section == fallbackSection))
            .FirstOrDefaultAsync(ct);
    }
}
