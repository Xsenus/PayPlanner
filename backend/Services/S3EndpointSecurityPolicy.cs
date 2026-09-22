using System.Net;
using Microsoft.Extensions.Options;

namespace PayPlanner.Api.Services;

public sealed class S3EndpointSecurityOptions
{
    public const string SectionName = "S3Storage:EndpointSecurity";
    public bool Enabled { get; set; } = true;
    public bool AllowHttp { get; set; }
    public bool AllowPrivateNetworks { get; set; }
    public List<string> AllowedHosts { get; set; } = [];
}

public interface IS3EndpointSecurityPolicy
{
    void ValidateConfiguration(string endpoint);
    Task ValidateResolvedAsync(string endpoint, CancellationToken cancellationToken);
}

public sealed class S3EndpointSecurityPolicy(IOptionsMonitor<S3EndpointSecurityOptions> options) : IS3EndpointSecurityPolicy
{
    public void ValidateConfiguration(string endpoint)
    {
        var settings = options.CurrentValue;
        if (!settings.Enabled) return;
        var uri = Parse(endpoint);
        if (uri.Scheme != Uri.UriSchemeHttps && !settings.AllowHttp)
            throw new S3StorageOperationException(400, "Endpoint S3 должен использовать HTTPS.");
        if (!string.IsNullOrWhiteSpace(uri.UserInfo) || !string.IsNullOrWhiteSpace(uri.Query) || !string.IsNullOrWhiteSpace(uri.Fragment))
            throw new S3StorageOperationException(400, "Endpoint S3 не должен содержать учётные данные, query или fragment.");
        if (IsAllowedHost(uri.Host, settings)) return;
        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(uri.Host, out var literal) && IsPrivateOrSpecial(literal) && !settings.AllowPrivateNetworks))
            throw new S3StorageOperationException(400, "Endpoint S3 указывает на локальный или закрытый сетевой адрес.");
    }

    public async Task ValidateResolvedAsync(string endpoint, CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        if (!settings.Enabled) return;
        ValidateConfiguration(endpoint);
        var uri = Parse(endpoint);
        if (IsAllowedHost(uri.Host, settings) || settings.AllowPrivateNetworks) return;

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException)
        {
            throw new S3StorageOperationException(502, "Не удалось безопасно разрешить DNS-имя endpoint S3.", ex);
        }
        if (addresses.Length == 0 || addresses.Any(IsPrivateOrSpecial))
            throw new S3StorageOperationException(400, "DNS endpoint S3 разрешился в локальный или закрытый сетевой адрес.");
    }

    private static Uri Parse(string endpoint)
        => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri
            : throw new S3StorageOperationException(400, "Endpoint S3 указан некорректно.");

    private static bool IsAllowedHost(string host, S3EndpointSecurityOptions settings)
        => settings.AllowedHosts.Any(allowed => string.Equals(allowed.Trim(), host, StringComparison.OrdinalIgnoreCase));

    public static bool IsPrivateOrSpecial(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal)
            return true;
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return (bytes[0] & 0xFE) == 0xFC;
        }
        var octets = address.GetAddressBytes();
        return octets[0] switch
        {
            0 or 10 or 127 or >= 224 => true,
            100 when octets[1] is >= 64 and <= 127 => true,
            169 when octets[1] == 254 => true,
            172 when octets[1] is >= 16 and <= 31 => true,
            192 when octets[1] == 168 => true,
            _ => false,
        };
    }
}

internal sealed class DisabledS3EndpointSecurityPolicy : IS3EndpointSecurityPolicy
{
    public void ValidateConfiguration(string endpoint) { }
    public Task ValidateResolvedAsync(string endpoint, CancellationToken cancellationToken) => Task.CompletedTask;
}
