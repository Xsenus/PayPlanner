using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

namespace PayPlanner.Api.Services;

/// <summary>
/// Централизованные ограничения публичных кабинетов, ссылок и callback.
/// Все окна разделяются по внешнему IP, не используют очередь и потому не дают
/// анонимным запросам бесконтрольно удерживать соединения приложения.
/// </summary>
public static class PublicAccessRateLimitPolicies
{
    public const string WorkOrderApproval = "public-work-order-approval";
    public const string ClientPortal = "client-portal";
    public const string MessengerDocumentDownload = "public-messenger-document-download";
    public const string MessengerDocumentCallback = "public-messenger-document-callback";

    /// <summary>Регистрирует политики с лимитами, соответствующими типу публичной нагрузки.</summary>
    public static void AddPolicies(RateLimiterOptions options)
    {
        AddFixedWindow(options, WorkOrderApproval, permitLimit: 10);
        AddFixedWindow(options, ClientPortal, permitLimit: 30);
        AddFixedWindow(options, MessengerDocumentDownload, permitLimit: 30);
        AddFixedWindow(options, MessengerDocumentCallback, permitLimit: 300);
    }

    private static void AddFixedWindow(RateLimiterOptions options, string policyName, int permitLimit)
    {
        options.AddPolicy(policyName, httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));
    }
}
