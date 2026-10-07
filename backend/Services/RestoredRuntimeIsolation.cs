using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;

namespace PayPlanner.Api.Services;

/// <summary>Transport boundary only; not a complete configuration for launching a restored CRM.</summary>
public static class RestoredRuntimeIsolation
{
    public const string EnabledKey = "RestoredRuntimeIsolation:Enabled";
    public const string BlockedMessage = "Внешние обращения запрещены в изолированной восстановленной среде.";

    public static void ThrowIfEnabled(IConfiguration? configuration)
    {
        if (configuration?.GetValue(EnabledKey, false) == true)
            throw new InvalidOperationException(BlockedMessage);
    }

    public static IServiceCollection AddRestoredRuntimeIsolation(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHttpMessageHandlerBuilderFilter, RestoredRuntimeHttpFilter>());
        // Configure this filter outermost so its handler is inserted after the
        // logging/authentication filters finish, ahead of their handlers.
        var filter = services.First(item => item.ServiceType == typeof(IHttpMessageHandlerBuilderFilter)
            && item.ImplementationType == typeof(RestoredRuntimeHttpFilter));
        services.Remove(filter);
        services.Insert(0, filter);
        return services;
    }
}

internal sealed class RestoredRuntimeHttpFilter(IConfiguration configuration) : IHttpMessageHandlerBuilderFilter
{
    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next)
        => builder =>
        {
            next(builder);
            builder.AdditionalHandlers.Insert(0, new RestoredRuntimeOutboundHandler(configuration));
        };
}

internal sealed class RestoredRuntimeOutboundHandler(IConfiguration configuration) : DelegatingHandler
{
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RestoredRuntimeIsolation.ThrowIfEnabled(configuration);
        return base.Send(request, cancellationToken);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RestoredRuntimeIsolation.ThrowIfEnabled(configuration);
        return base.SendAsync(request, cancellationToken);
    }
}
