using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;

namespace PayPlanner.Api.Extensions;

public static class UserAccessExtensions
{
    public static Task<bool> IsUserAdminAsync(this PaymentContext db, int userId, CancellationToken ct = default)
        => db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId && u.IsActive)
            .Select(u => u.Role != null && u.Role.Name == "admin")
            .FirstOrDefaultAsync(ct);
}
