using System.Security.Claims;

namespace PayPlanner.Api.Extensions;

public static class ClaimsPrincipalExtensions
{
    public static string GetDisplayName(this ClaimsPrincipal user)
    {
        if (user == null) return "";

        var name = user.FindFirstValue(ClaimTypes.Name);
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name.Trim();
        }

        var email = user.FindFirstValue(ClaimTypes.Email);
        if (!string.IsNullOrWhiteSpace(email))
        {
            return email.Trim();
        }

        return user.Identity?.Name?.Trim() ?? string.Empty;
    }
}
