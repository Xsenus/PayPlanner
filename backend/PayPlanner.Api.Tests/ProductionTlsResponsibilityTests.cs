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
