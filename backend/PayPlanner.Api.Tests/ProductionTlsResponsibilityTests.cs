using Xunit;

namespace PayPlanner.Api.Tests;

public class ProductionTlsResponsibilityTests
{
    [Fact]
    public void Program_DoesNotOwnHttpsRedirectOrHstsWhenTlsTerminatesAtProxy()
    {
        var root = FindRepositoryRoot();
        var program = File.ReadAllText(Path.Combine(root, "backend", "Program.cs"));

        Assert.DoesNotContain("UseHttpsRedirection", program);
        Assert.DoesNotContain("UseHsts", program);
    }

    [Fact]
    public void DeployRunbook_DocumentsTlsAndHstsResponsibility()
    {
        var root = FindRepositoryRoot();
        var runbook = File.ReadAllText(Path.Combine(root, "docs", "deploy-runbook.md"));

        Assert.Contains("nginx", runbook, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TLS", runbook, StringComparison.Ordinal);
        Assert.Contains("HSTS", runbook, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1:5080", runbook, StringComparison.Ordinal);
        Assert.Contains("Strict-Transport-Security", runbook, StringComparison.Ordinal);
    }

    [Fact]
    public void Program_MapsRealtimeSignalRHubs()
    {
        var root = FindRepositoryRoot();
        var program = File.ReadAllText(Path.Combine(root, "backend", "Program.cs"));

        Assert.Contains("app.MapHub<NotificationsHub>(NotificationsHub.Path)", program, StringComparison.Ordinal);
        Assert.Contains("app.MapHub<NotesHub>(NotesHub.Path)", program, StringComparison.Ordinal);
        Assert.Contains("NotificationsHub.Path", program, StringComparison.Ordinal);
        Assert.Contains("NotesHub.Path", program, StringComparison.Ordinal);
    }

    [Fact]
    public void DeployRunbook_DocumentsWebSocketProxyForSignalR()
    {
        var root = FindRepositoryRoot();
        var runbook = File.ReadAllText(Path.Combine(root, "docs", "deploy-runbook.md"));

        Assert.Contains("Realtime WebSocket Proxy", runbook, StringComparison.Ordinal);
        Assert.Contains("api.elitpravo.com", runbook, StringComparison.Ordinal);
        Assert.Contains("/hubs/notifications", runbook, StringComparison.Ordinal);
        Assert.Contains("/hubs/notes", runbook, StringComparison.Ordinal);
        Assert.Contains("map $http_upgrade $connection_upgrade", runbook, StringComparison.Ordinal);
        Assert.Contains("proxy_http_version 1.1", runbook, StringComparison.Ordinal);
        Assert.Contains("proxy_set_header Upgrade $http_upgrade", runbook, StringComparison.Ordinal);
        Assert.Contains("proxy_set_header Connection $connection_upgrade", runbook, StringComparison.Ordinal);
        Assert.Contains("proxy_read_timeout 3600s", runbook, StringComparison.Ordinal);
        Assert.Contains("access_log off", runbook, StringComparison.Ordinal);
        Assert.Contains("payplanner-hubs-error.log crit", runbook, StringComparison.Ordinal);
        Assert.Contains("Do not write regular access logs for the SignalR hub locations", runbook, StringComparison.Ordinal);
        Assert.Contains("signalr_token_redacted", runbook, StringComparison.Ordinal);
        Assert.Contains("After sanitizing, confirm that no raw hub access-token parameter remains", runbook, StringComparison.Ordinal);
        Assert.Contains("101 Switching Protocols", runbook, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "README.md"))
                && File.Exists(Path.Combine(current.FullName, "backend", "Program.cs")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
