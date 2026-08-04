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
    public const string ExternalSettingsPolicy = "auth-external-settings";
    public const string ExternalStartPolicy = "auth-external-start";
    public const string ExternalStatusPolicy = "auth-external-status";
    public const string ExternalCallbackPolicy = "auth-external-callback";
    public const string TelegramWebhookPolicy = "auth-telegram-webhook";

    /// <summary>Добавляет политики внутреннего входа, регистрации и внешней авторизации.</summary>
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
        AddFixedWindowPolicy(options, ExternalSettingsPolicy, permitLimit: 60, TimeSpan.FromMinutes(1));
        AddFixedWindowPolicy(options, ExternalStartPolicy, permitLimit: 10, TimeSpan.FromMinutes(1));
        AddFixedWindowPolicy(options, ExternalStatusPolicy, permitLimit: 120, TimeSpan.FromMinutes(1));
        AddFixedWindowPolicy(options, ExternalCallbackPolicy, permitLimit: 30, TimeSpan.FromMinutes(1));
        AddFixedWindowPolicy(options, TelegramWebhookPolicy, permitLimit: 300, TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// Регистрирует фиксированное окно с общей схемой разделения по внешнему IP.
    /// Очередь намеренно отключена: при превышении лимита клиент сразу получает 429
    /// и не удерживает соединение или память приложения.
    /// </summary>
    private static void AddFixedWindowPolicy(
        RateLimiterOptions options,
        string policyName,
        int permitLimit,
        TimeSpan window)
    {
        options.AddPolicy(policyName, httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = window,
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));
    }
}
