using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;

namespace PayPlanner.Api.Extensions;

public static class FinancialActionReasonExtensions
{
    public static async Task<bool> RequiresFinancialActionReasonAsync(
        this ClaimsPrincipal user,
        PaymentContext db,
        CancellationToken cancellationToken = default)
    {
        var userId = user.GetUserId();
        if (!userId.HasValue)
        {
            return false;
        }

        return await db.Users
            .AsNoTracking()
            .Where(item => item.Id == userId.Value)
            .Select(item => item.RequiresFinancialActionReason)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
