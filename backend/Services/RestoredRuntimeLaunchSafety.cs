using System.Net;

namespace PayPlanner.Api.Services;

/// <summary>Early configuration checks only; restored database identity and key isolation require separate verification.</summary>
public static class RestoredRuntimeLaunchSafety
{
    public static void Validate(IConfiguration configuration, string effectiveUrls)
    {
        if (!configuration.GetValue(RestoredRuntimeIsolation.EnabledKey, false)) return;

        RequireDisabled(configuration, "BackgroundServices:Enabled");
        RequireDisabled(configuration, "Email:Enabled");
        RestoredRuntimeDatabaseIdentity.RequireInstanceId(configuration);

        var addresses = effectiveUrls.Split(';', StringSplitOptions.TrimEntries);
        foreach (var address in addresses)
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https")
                || !string.IsNullOrEmpty(uri.UserInfo)
                || uri.AbsolutePath != "/"
                || !string.IsNullOrEmpty(uri.Query)
                || !string.IsNullOrEmpty(uri.Fragment)
                || !IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip)
                || !IPAddress.IsLoopback(ip))
                throw new InvalidOperationException("Изолированная восстановленная среда допускает только HTTP/HTTPS-адреса с явным loopback IP.");
        }
    }

    private static void RequireDisabled(IConfiguration configuration, string key)
    {
        if (configuration.GetValue<bool?>(key) != false)
            throw new InvalidOperationException($"Для изолированной восстановленной среды требуется {key}=false.");
    }
}
