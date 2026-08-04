using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using System.Security.Claims;

namespace PayPlanner.Api.Services;

public sealed class CurrentUserClaimsTransformation : IClaimsTransformation
{
    private readonly IDbContextFactory<PaymentContext> _dbContextFactory;

    public CurrentUserClaimsTransformation(IDbContextFactory<PaymentContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity identity || !identity.IsAuthenticated)
        {
            return principal;
        }

        var userId = principal.GetUserId();
        if (!userId.HasValue)
        {
            return principal;
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var snapshot = await db.Users
            .AsNoTracking()
            .Where(user => user.Id == userId.Value)
            .Select(user => new
            {
                user.Email,
                user.FullName,
                user.IsActive,
                user.IsApproved,
                RoleName = user.Role != null ? user.Role.Name : "user",
            })
            .FirstOrDefaultAsync();

        if (snapshot is null || !snapshot.IsActive || !snapshot.IsApproved)
        {
            // A signed JWT must stop granting access as soon as the employee is
            // removed, disabled or loses approval. Returning an anonymous
            // principal signals the JWT validation hook to reject the token with
            // 401 instead of trusting the stale claims until it expires.
            return new ClaimsPrincipal(new ClaimsIdentity());
        }

        ReplaceSingleClaim(identity, ClaimTypes.Email, snapshot.Email);
        ReplaceSingleClaim(identity, ClaimTypes.Name, snapshot.FullName);
        ReplaceSingleClaim(identity, ClaimTypes.Role, snapshot.RoleName);

        return principal;
    }

    private static void ReplaceSingleClaim(ClaimsIdentity identity, string claimType, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var existing = identity.FindAll(claimType).ToList();
        if (existing.Count == 1 && string.Equals(existing[0].Value, value, StringComparison.Ordinal))
        {
            return;
        }

        foreach (var claim in existing)
        {
            identity.RemoveClaim(claim);
        }

        identity.AddClaim(new Claim(claimType, value));
    }
}
