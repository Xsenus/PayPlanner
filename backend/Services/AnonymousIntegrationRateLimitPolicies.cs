using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

namespace PayPlanner.Api.Services;

/// <summary>
/// Ограничения для анонимных callback, webhook и публичных служебных ответов.
/// Ключ включает маршрут, чтобы разные интеграции одного внешнего провайдера
/// не расходовали общую корзину запросов.
/// </summary>
public static class AnonymousIntegrationRateLimitPolicies
{
    public const string OAuthCallback = "anonymous-integration-oauth-callback";
    public const string Webhook = "anonymous-integration-webhook";
    public const string PublicMetadata = "anonymous-integration-public-metadata";

    /// <summary>Регистрирует ограничения без очереди и накопления соединений.</summary>
    public static void AddPolicies(RateLimiterOptions options)
    {
        AddFixedWindow(options, OAuthCallback, permitLimit: 30);
        AddFixedWindow(options, Webhook, permitLimit: 300);
        AddFixedWindow(options, PublicMetadata, permitLimit: 600);
    }

    private static void AddFixedWindow(RateLimiterOptions options, string policyName, int permitLimit)
    {
        options.AddPolicy(policyName, httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                $"{httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"}:{httpContext.Request.Path.Value}",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));
    }
}
