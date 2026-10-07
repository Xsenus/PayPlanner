using System.Net;

namespace PayPlanner.Api.Services.Notifications;

internal static class TelegramBotApiHttpClientFactory
{
    public static HttpClient Create(IHttpClientFactory httpClientFactory, TelegramBotConnectionSettings? connection,
        IConfiguration? configuration = null)
    {
        RestoredRuntimeIsolation.ThrowIfEnabled(configuration);
        if (string.IsNullOrWhiteSpace(connection?.ProxyUrl))
        {
            return httpClientFactory.CreateClient();
        }

        var proxy = new WebProxy(connection.ProxyUrl.Trim());
        if (!string.IsNullOrWhiteSpace(connection.ProxyUsername))
        {
            proxy.Credentials = new NetworkCredential(
                connection.ProxyUsername.Trim(),
                connection.ProxyPassword ?? string.Empty);
        }

        return new HttpClient(new HttpClientHandler
        {
            Proxy = proxy,
            UseProxy = true
        }, disposeHandler: true);
    }

    public static TelegramBotConnectionSettings ToConnectionSettings(this TelegramSenderConfig config)
        => new(config.ProxyUrl, config.ProxyUsername, config.ProxyPassword);
}
