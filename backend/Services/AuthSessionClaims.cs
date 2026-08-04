using System.Globalization;
using System.Security.Claims;

namespace PayPlanner.Api.Services;

/// <summary>
/// Единый контракт версии серверной сессии внутри JWT.
/// Отсутствующую версию старых токенов трактует как нулевую, поэтому публикация
/// механизма не завершает действующие сессии до их явного отзыва пользователем.
/// </summary>
public static class AuthSessionClaims
{
    public const string TokenVersionClaimType = "payplanner:auth-token-version";

    /// <summary>Создаёт claim с текущей версией сессии пользователя.</summary>
    public static Claim CreateTokenVersionClaim(int tokenVersion)
        => new(TokenVersionClaimType, tokenVersion.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Читает единственную корректную неотрицательную версию. Для JWT,
    /// выпущенных до появления механизма, возвращает версию 0.
    /// </summary>
    public static bool TryGetTokenVersion(ClaimsPrincipal principal, out int tokenVersion)
    {
        var claims = principal.FindAll(TokenVersionClaimType).ToList();
        if (claims.Count == 0)
        {
            tokenVersion = 0;
            return true;
        }

        tokenVersion = -1;
        return claims.Count == 1
            && int.TryParse(claims[0].Value, NumberStyles.None, CultureInfo.InvariantCulture, out tokenVersion)
            && tokenVersion >= 0;
    }
}
