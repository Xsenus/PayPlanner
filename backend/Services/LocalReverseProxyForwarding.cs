using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using System.Net;

namespace PayPlanner.Api.Services;

/// <summary>Настраивает безопасное получение схемы и IP клиента от локального nginx.</summary>
public static class LocalReverseProxyForwarding
{
    /// <summary>
    /// Доверяет ровно одному прокси-переходу с loopback-адреса.
    /// Заголовки от прямого внешнего подключения при этом не считаются доверенными.
    /// </summary>
    public static void Configure(ForwardedHeadersOptions options)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        options.KnownProxies.Add(IPAddress.Loopback);
        options.KnownProxies.Add(IPAddress.IPv6Loopback);
    }
}
