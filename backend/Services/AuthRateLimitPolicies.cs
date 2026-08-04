using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

namespace PayPlanner.Api.Services;

/// <summary>
/// Единые имена и параметры ограничения публичных запросов внутренней авторизации.
/// Разделение по уже проверенному адресу клиента защищает вход от перебора,
/// не объединяя всех пользователей за локальным nginx в одну корзину.
/// </summary>
public static class AuthRateLimitPolicies
{
    public const string LoginPolicy = "auth-login";
    public const string RegisterPolicy = "auth-register";

    /// <summary>Добавляет политики входа и самостоятельной регистрации.</summary>
    public static void AddPolicies(RateLimiterOptions options)
    {
        options.AddPolicy(LoginPolicy, httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 30,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));
        options.AddPolicy(RegisterPolicy, httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 5,
                    Window = TimeSpan.FromMinutes(10),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));
    }
}
